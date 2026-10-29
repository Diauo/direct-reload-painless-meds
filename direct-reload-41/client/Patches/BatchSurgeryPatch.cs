using System;
using System.Reflection;
using EFT;
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
///
/// 耐久校验/退款思路参考：Kirisame dish (kirisamepositive) — RZMedTiming (MIT)
///   https://github.com/WYYQWQ1/SPT-RZMedTiming
///   ①「先恢复 → 回读 IsBodyPartDestroyed → 确认治好才扣」，异常绝不扣，写绝对值防二次扣；
///   ②「一个肢体都没治好就被取消」时可选退还原版扣掉的 1 点耐久。
///   本文件为独立实现；详见 ACKNOWLEDGEMENTS.md。
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

    // ── 手术中断判定（用于「取消退耐久」）────────────────────────
    // 本次 arm 时的耐久；负值 = 当前没有进行中的手术。
    private static float _armedResource = -1f;
    // 本次操作里实际恢复成功的肢体数。
    private static int _restoredInOp;
    // BodyPartRestoredEvent 是否触发过（= 至少有一个肢体被治好）。
    private static bool _triggered;

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
        _armedResource = medical.MedKitComponent.HpResource;
        _restoredInOp = 0;
        _triggered = false;
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
        _triggered = true;

        var penalty = GetSurgeryPenalty(kit);
        var restoredCount = 0;
        var medKit = kit.MedKitComponent;

        foreach (var part in HealthHelper.RealBodyParts)
        {
            var before = medKit.HpResource;
            if (before < 2f)
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

            bool ok;
            try
            {
                ok = controller.RestoreBodyPart(part, penalty);
            }
            catch (Exception ex)
            {
                // 调用抛异常：绝不扣耐久
                medKit.HpResource = before;
                Plugin.Log.LogError($"[DirectReload] Batch surgery: {part} threw -> resource kept at {before}: {ex.Message}");
                continue;
            }

            // 关键：以「回读」为准，而不是以返回值为准。
            // 返回 true 但肢体仍是黑的（写入没落地 / 被后续逻辑覆盖）时绝不扣耐久。
            var stillDestroyed = true;
            try
            {
                stillDestroyed = controller.IsBodyPartDestroyed(part);
            }
            catch
            {
                // 回读失败按「没恢复」处理，宁可少扣也不白扣
            }

            if (ok && !stillDestroyed)
            {
                // 写绝对值而不是 -=1：避免别处同时改耐久时二次扣减
                medKit.HpResource = before - 1f;
                restoredCount++;
                _restoredInOp++;
                RefreshKit(medKit);

                if (Plugin.DebugLog.Value)
                {
                    Plugin.Log.LogInfo($"[DirectReload] Batch surgery: restored {part}, resource {before} -> {medKit.HpResource}");
                }
            }
            else
            {
                medKit.HpResource = before;

                if (Plugin.DebugLog.Value)
                {
                    Plugin.Log.LogWarning(
                        $"[DirectReload] Batch surgery: {part} NOT committed (ok={ok}, stillDestroyed={stillDestroyed}) -> resource kept at {before}");
                }
            }
        }

        if (Plugin.DebugLog.Value)
        {
            Plugin.Log.LogInfo($"[DirectReload] Batch surgery complete: {restoredCount} limb(s) restored, resource left {medKit.HpResource}");
        }
    }

    /// <summary>
    /// 操作收尾（正常完成 / 取消 / 移除）：若本次 arm 过、却一个肢体都没治好而耐久已经掉了
    /// （= 被移动或取消打断），按配置退还游戏扣掉的那 1 点。默认关闭 = 原版行为。
    /// </summary>
    internal static void OnOperationTeardown(string reason)
    {
        try
        {
            if (_armedResource < 0)
            {
                return; // 没有进行中的批量手术
            }

            var medKit = _kit?.MedKitComponent;
            if (medKit == null)
            {
                return;
            }

            var now = medKit.HpResource;
            var healed = _triggered && _restoredInOp > 0;
            var charged = now < _armedResource;
            var refunded = false;

            if (!healed && charged && Plugin.RefundSurgeryOnCancel.Value)
            {
                medKit.HpResource = now + 1f;
                RefreshKit(medKit);
                refunded = true;
            }

            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogInfo(
                    $"[DirectReload] Batch surgery teardown: reason={reason}, armed={_armedResource}, restored={_restoredInOp}, " +
                    $"resourceNow={now}, healed={healed}, charged={charged}, refunded={refunded}");
            }

            if (refunded)
            {
                Plugin.Log.LogInfo("[DirectReload] Batch surgery cancelled before healing — 1 durability refunded");
            }
        }
        catch (Exception ex)
        {
            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogWarning($"[DirectReload] Batch surgery teardown failed: {ex.Message}");
            }
        }
        finally
        {
            _armedResource = -1;
            _restoredInOp = 0;
            _triggered = false;
        }
    }

    /// <summary>耐久变动后让库存 UI 立即刷新（否则数字要等下一次打开面板才更新）。</summary>
    private static void RefreshKit(MedKitComponent medKit)
    {
        try
        {
            medKit.Item?.RaiseRefreshEvent();
        }
        catch
        {
            // UI 刷新失败不影响手术本身
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

/// <summary>
/// 手术操作的收尾钩子定位：Finish（正常完成）、Reset（操作复用）、Remove（被移除）。
/// 只用于判定「本次手术是否被取消」，主体逻辑在 <see cref="BatchSurgeryPatch.OnOperationTeardown"/>。
/// </summary>
internal static class SurgicalTeardown
{
    internal static MethodBase Target(string methodName)
    {
        var operationType = typeof(Player.MedsController).GetNestedType(
            "MedsInHandsOperation", BindingFlags.Public | BindingFlags.NonPublic);

        if (operationType == null)
        {
            throw new InvalidOperationException("[DirectReload] MedsInHandsOperation not found — game version mismatch?");
        }

        return AccessTools.Method(operationType, methodName)
               ?? throw new InvalidOperationException(
                   $"[DirectReload] MedsInHandsOperation.{methodName} not found — game version mismatch?");
    }
}

internal class SurgicalOperationFinishPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => SurgicalTeardown.Target("Finish");

    [PatchPrefix]
    private static void Prefix() => BatchSurgeryPatch.OnOperationTeardown("finish");
}

internal class SurgicalOperationResetPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => SurgicalTeardown.Target("Reset");

    [PatchPrefix]
    private static void Prefix() => BatchSurgeryPatch.OnOperationTeardown("reset");
}

internal class SurgicalOperationRemovePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() => SurgicalTeardown.Target("Remove");

    [PatchPrefix]
    private static void Prefix() => BatchSurgeryPatch.OnOperationTeardown("remove");
}
