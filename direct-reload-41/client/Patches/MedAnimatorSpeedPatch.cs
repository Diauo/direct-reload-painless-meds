using System;
using System.Reflection;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 全医疗动画真加速（SPT 4.1）——直接写 Animator.speed，对整个动画剪辑生效。
///
/// 为什么不用 FirearmsAnimator.SetUseTimeMultiplier：
///   游戏在 MedsController.Setup / MedsInHandsOperation.GoNextBodyPart 里把医疗/手术倍率写进
///   animator 的 FLOAT_USETIMEMULTIPLIER 参数，但该方法内部先做 HasParameter 检查、没有该参数就
///   静默 return（~195629）。手术包的预制体吃这个参数，夹板 / 注射器等不吃 —— 于是效果计时按
///   服务端 medUseTime 提前到期，MedEffect 被移除后 OnEffectRemoved → Finish() → SetActiveParam(false)
///   把动画硬切到收尾段，表现为「展开到一半又缩回去」。
///   Animator.speed 与参数无关，任何剪辑都会被真正加速。
///
/// 写入点：MedsController.Spawn —— 游戏刚用自身 animationSpeed 设过基线，我们在此基础上乘倍率。
/// 清理：游戏在 RemoveHandsOperation.Start 里会 SetAnimationSpeed(1f)，无需我们恢复；同一预制体
///   下次使用时 Spawn 也会重设基线，因此不存在倍率残留。
/// 范围：只处理本地玩家（bot / Fika 远端玩家也会走同一个 Spawn，必须用 IsYourPlayer 拦掉）。
///
/// Credit: 「技能加成取本地玩家现读、读不到按 0 绝不兜底 0.4」的做法参考
///   Kirisame dish (kirisamepositive) — RZMedTiming (MIT) https://github.com/WYYQWQ1/SPT-RZMedTiming 。
///   「别用 SetUseTimeMultiplier 驱动医疗动画」这一判断来自该 mod 内合并的
///   QuickMeds（BLACK HAWK, MIT）代码注释；此处为独立实现。详见 ACKNOWLEDGEMENTS.md。
/// </summary>
internal class MedAnimatorSpeedPatch : ModulePatch
{
    /// <summary>倍率的合理区间——防止配置写错（0 或极大值）把动画搞成冻结/瞬移。</summary>
    private const float MinScale = 0.1f;
    private const float MaxScale = 10f;

    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
                   typeof(Player.MedsController),
                   nameof(Player.MedsController.Spawn),
                   new[] { typeof(float), typeof(Action) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] Player.MedsController.Spawn(float, Action) not found — game version mismatch?");
    }

    [PatchPostfix]
    private static void Postfix(Player.MedsController __instance, float animationSpeed)
    {
        if (!Plugin.EnableMedAnimSync.Value)
        {
            return;
        }

        try
        {
            var player = __instance._player;
            if (player == null || !player.IsYourPlayer)
            {
                return; // bot / 远端玩家的控制器不参与
            }

            var scale = Plugin.MedAnimSpeedScale.Value;
            if (scale < MinScale)
            {
                scale = MinScale;
            }
            else if (scale > MaxScale)
            {
                scale = MaxScale;
            }

            if (Math.Abs(scale - 1f) < 0.001f)
            {
                return; // 1.0 = 原版，不写
            }

            var target = animationSpeed * scale;
            if (animationSpeed <= 0.01f)
            {
                // 游戏传 0 / 负值属于异常路径：按 1.0 兜底，绝不让动画冻结
                target = scale;
            }

            target *= SkillRatio(player, __instance.Item);

            __instance.FirearmsAnimator?.SetAnimationSpeed(target);

            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogInfo(
                    $"[DirectReload] MedAnim: {__instance.Item?.StringTemplateId} " +
                    $"animator speed {animationSpeed:0.000} -> {target:0.000} " +
                    $"(x{scale:0.00}, skillRatio {target / (scale * (animationSpeed <= 0.01f ? 1f : animationSpeed)):0.000})");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DirectReload] MedAnim animator speed apply failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 手术技能的时长补偿比值。
    ///
    /// 游戏两处时钟对同一技能加成用了不同单位：
    ///   治疗时长（DoMedEffect）：num /= 1 + Skills.SurgerySpeed.Value          ← 满级 1.4
    ///   动画参数（GoNextBodyPart）：1 + Skills.SurgerySpeed.Value / 100        ← 满级 1.004
    /// 手术包预制体吃这个参数，所以把比值 (1+s)/(1+s/100) 乘到 Animator.speed 上，
    /// 最终动画倍率 = 参数 × 比值 = (1+s)，与治疗时长一一对应（已有 ×2 的服务端倍率独立相乘）。
    ///
    /// 只对「能治黑肢」的物品生效：普通医疗包 / 夹板的时长里没有 /(1+技能) 这一项，
    /// 一并乘上就会变成动画超前。
    /// </summary>
    private static float SkillRatio(Player player, Item? item)
    {
        try
        {
            if (item is not Meds meds || meds.HealthEffectsComponent == null)
            {
                return 1f;
            }

            if (!meds.HealthEffectsComponent.AffectsAny(EDamageEffectType.DestroyedPart))
            {
                return 1f; // 非手术包：时长不含技能项
            }

            var skills = player.Skills;
            if (skills == null)
            {
                return 1f;
            }

            var bonus = skills.SurgerySpeed.Value;
            if (bonus <= 0f)
            {
                return 1f; // 读不到 / 0 级：不补偿，绝不用 0.4 兜底
            }

            if (bonus > 0.5f)
            {
                bonus = 0.5f; // 防脏数据
            }

            return (1f + bonus) / (1f + bonus / 100f);
        }
        catch
        {
            return 1f;
        }
    }
}
