using System;
using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 诊断用（只观察，不改值）：记录游戏自己写进 FirearmsAnimator.SetUseTimeMultiplier 的原始倍率。
///
/// 4.1 起动画加速改由 MedAnimatorSpeedPatch 直接写 Animator.speed —— 该参数在夹板 / 注射器
/// 等预制体上不生效（游戏写入前有 HasParameter 检查，缺参数就静默 return），这也是
/// 「效果先到期、动画被切在展开一半」的根因。此处若继续乘倍率，手术包会与 Animator.speed
/// 叠加成双重加速，因此只保留日志。
/// </summary>
internal class MedAnimSpeedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("FirearmsAnimator")
                   ?? throw new InvalidOperationException(
                       "[DirectReload] Type 'FirearmsAnimator' not found — game version mismatch?");

        return AccessTools.Method(type, "SetUseTimeMultiplier", new[] { typeof(float) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] Method 'FirearmsAnimator.SetUseTimeMultiplier(float)' not found — game version mismatch?");
    }

    [PatchPrefix]
    private static void Prefix(float speed)
    {
        if (!Plugin.DebugLog.Value || !Plugin.EnableMedAnimSync.Value)
        {
            return;
        }

        try
        {
            Plugin.Log.LogInfo($"[DirectReload] MedAnim: SetUseTimeMultiplier(game) = {speed:0.000} (pass-through)");
        }
        catch
        {
            // 诊断日志失败不影响游戏
        }
    }
}
