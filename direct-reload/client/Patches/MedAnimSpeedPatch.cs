using System.Reflection;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 医疗/手术动画速度同步。
///
/// 背景：服务端缩短了医疗品 medUseTime（使用时间）后，
/// "效果"会提前完成，但动画仍按原速播放（效果完成、动画还在播）。
///
/// 本 Patch 把游戏原生的"使用时间倍率"（SetUseTimeMultiplier——
/// 原用于外科技能加速手术动画）同步乘以我们的加速比例，
/// 使动画与效果在同一时间完成。
///
/// 倍率换算：动画速度倍率 = 1 / 服务端 medUseTime 倍率
///   例如服务端 multiplier=0.5（时间减半）→ 本项填 2.0（动画两倍速）
/// </summary>
internal class MedAnimSpeedPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(typeof(FirearmsAnimator), nameof(FirearmsAnimator.SetUseTimeMultiplier));
    }

    [PatchPrefix]
    private static void Prefix(ref float speed)
    {
        if (!Plugin.EnableMedAnimSync.Value)
        {
            return;
        }

        var scale = Plugin.MedAnimSpeedScale.Value;
        if (scale > 0f)
        {
            speed *= scale;
        }
    }
}
