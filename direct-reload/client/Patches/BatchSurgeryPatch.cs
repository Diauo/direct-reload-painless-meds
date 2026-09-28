using System;
using System.Reflection;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 批量手术：使用手术包（CMS / Surv12 / Sanitar）时，
/// 一次使用依次治疗所有残疾肢体，每个肢体消耗 1 点耐久。
///
/// 触发：稳定——任意使用方式（快捷键 / 双击 / 右键菜单 / 拖拽）均生效。
/// 机制：在"手术目标确定"（GClass3010.method_7）后注册"恢复链"：
///   每次一个肢体恢复完成 → 自动寻找下一个黑肢 → RestoreBodyPart + 耐久 -1 → 继续，
///   直到没有黑肢或耐久不足。
///
/// 目标环境：SPT 4.0.13 / EFT 0.16.9.40087
/// （patch 目标与 SoftCoreMeds 相同：GClass3010 的 method_7；本实现为"无条件批量"）
/// </summary>
internal class BatchSurgeryPatch : ModulePatch
{
    /// <summary>手术包模板（CMS / Surv12 / Sanitar）</summary>
    private static readonly string[] SurgicalKitIds =
    {
        "5d02778e86f774203e7dedbe", // CMS
        "5d02797c86f774203f38e30a", // Surv12
        "5e99735686f7744bfc4af32c", // Sanitar's kit
    };

    private static GClass3010 _controller;
    private static MedsItemClass _kit;

    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("GClass3010")
                   ?? throw new InvalidOperationException(
                       "[DirectReload] Type 'GClass3010' not found — game version mismatch?");

        return AccessTools.Method(
            type,
            "method_7",
            new[] { typeof(Item), typeof(EBodyPart), typeof(bool), typeof(EBodyPart?).MakeByRefType() })
               ?? throw new InvalidOperationException(
                   "[DirectReload] Method 'GClass3010.method_7(Item, EBodyPart, bool, out EBodyPart?)' not found — game version mismatch?");
    }

    [PatchPostfix]
    private static void Postfix(
        GClass3010 __instance,
        Item item,
        EBodyPart bodyPart,
        bool fastSearch,
        ref EBodyPart? damagedBodyPart,
        ref bool __result)
    {
        if (!Plugin.EnableBatchSurgery.Value)
        {
            return;
        }

        if (fastSearch)
        {
            // UI 的模拟搜索（dry run）——跳过
            return;
        }

        if (item is not MedsItemClass medical || medical.MedKitComponent == null)
        {
            return;
        }

        if (!IsSurgicalKit(item.StringTemplateId))
        {
            return;
        }

        // 耐久太少（<=1）时不做批量（留 1 点给当前这次手术）
        if (medical.MedKitComponent.HpResource <= 1)
        {
            return;
        }

        if (Plugin.DebugLog.Value)
        {
            Plugin.Log.LogInfo($"[DirectReload] Batch surgery armed (kit resource: {medical.MedKitComponent.HpResource})");
        }

        // 注册恢复链（先移除再添加，防止重复注册）
        _controller = __instance;
        _kit = medical;
        __instance.BodyPartRestoredEvent -= OnBodyPartRestored;
        __instance.BodyPartRestoredEvent += OnBodyPartRestored;
    }

    /// <summary>第一个肢体恢复完成 → 一次性恢复所有剩余黑肢（同一时刻，无排队）</summary>
    private static void OnBodyPartRestored(EBodyPart body, ValueStruct health)
    {
        var controller = _controller;
        var kit = _kit;
        if (controller == null || kit == null || kit.MedKitComponent == null)
        {
            return;
        }

        // 摘钩：本次一次性处理所有肢体（内部恢复触发的事件不再回调，防止重入）
        controller.BodyPartRestoredEvent -= OnBodyPartRestored;

        var penalty = GetSurgeryPenalty(kit);
        var restoredCount = 0;

        // ★ 一次性循环恢复所有黑肢（同步完成——"一次使用治疗所有肢体"）
        foreach (var part in GClass3058.RealBodyParts)
        {
            if (kit.MedKitComponent.HpResource < 2)
            {
                if (Plugin.DebugLog.Value)
                {
                    Plugin.Log.LogInfo("[DirectReload] Batch surgery stopped: not enough resource");
                }
                break;
            }

            if (!controller.IsBodyPartDestroyed(part))
            {
                continue;
            }

            try
            {
                if (controller.RestoreBodyPart(part, penalty))
                {
                    kit.MedKitComponent.HpResource -= 1;
                    restoredCount++;

                    if (Plugin.DebugLog.Value)
                    {
                        Plugin.Log.LogInfo($"[DirectReload] Batch surgery: restored {part}, resource left {kit.MedKitComponent.HpResource}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectReload] Batch surgery step failed: {ex}");
            }
        }

        if (Plugin.DebugLog.Value)
        {
            Plugin.Log.LogInfo($"[DirectReload] Batch surgery complete: {restoredCount} limb(s) restored");
        }
    }

    /// <summary>读取手术包的"手术后保留比例"（与服务端模板一致：100/100 = 无惩罚）</summary>
    private static float GetSurgeryPenalty(MedsItemClass kit)
    {
        try
        {
            var effects = kit.HealthEffectsComponent?.DamageEffects;
            if (effects != null
                && effects.TryGetValue(EDamageEffectType.DestroyedPart, out var effect)
                && effect != null)
            {
                var min = (float)effect.HealthPenaltyMin;
                var max = (float)effect.HealthPenaltyMax;
                if (max > 0f || min > 0f)
                {
                    return UnityEngine.Random.Range(min, max) / 100f;
                }
            }
        }
        catch
        {
            // 读取失败时退回 1f（完全保留——与服务端数据一致的兜底）
        }

        return 1f;
    }

    private static bool IsSurgicalKit(string templateId)
    {
        foreach (var id in SurgicalKitIds)
        {
            if (id == templateId)
            {
                return true;
            }
        }

        return false;
    }
}
