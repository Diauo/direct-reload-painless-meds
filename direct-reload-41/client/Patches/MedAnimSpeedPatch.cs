using System;
using System.Reflection;
using EFT.Animations;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 医疗动画加速同步（SPT 4.1）：补丁 FirearmsAnimator.SetUseTimeMultiplier(float)，
/// 把医疗/手术动画速度乘以配置倍率（= 1 / 服务端 medUseTime multiplier）。
/// 只有医疗代码调用该方法，全局乘系数是安全的。
/// 4.1 该类型与方法均为真名，直接强类型目标。
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
    private static void Prefix(ref float speed)
    {
        if (!Plugin.EnableMedAnimSync.Value)
        {
            return;
        }

        try
        {
            speed *= Plugin.MedAnimSpeedScale.Value;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] MedAnimSpeed prefix failed: {ex}");
        }
    }
}
