using System;
using System.Reflection;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 批量手术 — SPT 4.1 版（强类型，与 SoftCoreMeds 4.1 同目标）。
/// 目标：EFT.HealthSystem.PlayerHealthController.TryGetBodyPartToApply(Item, EBodyPart, bool, out EBodyPart?)
///   （4.0: GClass3010.method_7 —— 语义不变）。
/// 机制：在"手术目标确定"后挂 BodyPartRestoredEvent 恢复链；首个肢体恢复完成 → 一次性同步恢复所有黑肢
/// （每个消耗 1 点耐久）。触发方式稳定：快捷键 / 双击 / 右键菜单 / 拖拽均生效（无条件批量）。
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

    private static PlayerHealthController? _controller;
    private static Meds? _kit;

    protected override MethodBase GetTargetMethod()
    {
        // 方法声明在泛型基类上（BaseHealthController<TEffect>），
        // 直接瞄准声明处（HarmonyX 建议），避免"patch 继承方法"的警告。
        var paramTypes = new[] { typeof(Item), typeof(EBodyPart), typeof(bool), typeof(EBodyPart?).MakeByRefType() };
        var baseType = typeof(BaseHealthController<ActiveHealthController.Effect>);

        return AccessTools.Method(baseType, "TryGetBodyPartToApply", paramTypes)
               ?? AccessTools.Method(typeof(PlayerHealthController), "TryGetBodyPartToApply", paramTypes)
               ?? throw new InvalidOperationException(
                   "[DirectReload] TryGetBodyPartToApply not found — game version mismatch?");
    }

    [PatchPostfix]
    private static void Postfix(
        PlayerHealthController __instance,
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

        if (item is not Meds medical || medical.MedKitComponent == null)
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

        _controller = __instance;
        _kit = medical;
        __instance.BodyPartRestoredEvent -= OnBodyPartRestored;
        __instance.BodyPartRestoredEvent += OnBodyPartRestored;
    }

    /// <summary>第一个肢体恢复完成 → 一次性恢复所有剩余黑肢（同步完成，无排队）</summary>
    private static void OnBodyPartRestored(EBodyPart body, ValueStruct health)
    {
        var controller = _controller;
        var kit = _kit;
        if (controller == null || kit?.MedKitComponent == null)
        {
            return;
        }

        // 摘钩：本次一次性处理所有肢体（内部恢复触发的事件不再回调，防止重入）
        controller.BodyPartRestoredEvent -= OnBodyPartRestored;

        var penalty = GetSurgeryPenalty(kit);
        var restoredCount = 0;

        foreach (var part in HealthHelper.RealBodyParts)
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
    private static float GetSurgeryPenalty(Meds kit)
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
