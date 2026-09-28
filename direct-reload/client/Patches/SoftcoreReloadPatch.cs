using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 潜行者式装填：保留枪上弹匣，消耗可触及位置（口袋/弹挂）的散装弹药，
/// 播放一次原版换弹动画，并在动画完成后装满弹匣。
/// 覆盖模式：ExternalMagazine + ExternalMagazineWithInternalReloadSupport（可拆弹匣且支持直装）。
/// 纯弹仓武器（InternalMagazine：莫辛 / 固定弹仓 SKS 等）保持原版逐发装填——
/// 拟真与软核的平衡点：可换弹匣的才享受软核直装。
/// 失败通知按游戏界面语言自动本地化（中文界面 → 中文；其他 → 英文）。
///
/// v1.0.2（Fika 兼容定版）：补填 + 补膛回到本地提交（与官方 CommitReloadWithAmmo 相同的原语，
/// 共享实现见 AmmoCommit），并在宿主端以 FikaHostReloadMirrorPatch 对玩家副本执行同一组提交 ——
/// Fika 对装填类变更的原生并行提交模式。曾用网络操作通道提交，但作用于在手上武器弹匣的库存操作
/// 会被游戏武器操作校验器拒绝（客户端失败 / 宿主成功 → 状态撕裂），不可行。
/// 单机与 Fika 会话行为一致；双方物品 ID 序列锁步，各自铸出的物品一致。
/// Target: SPT 4.0.13 / EFT 0.16.9.40087
/// </summary>
internal class SoftcoreReloadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName("Class1730")
                   ?? throw new InvalidOperationException(
                       "[DirectReload] Type 'Class1730' not found — game version mismatch?");

        return AccessTools.Method(type, "method_17", new[] { typeof(Weapon), typeof(bool) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] Method 'Class1730.method_17(Weapon, bool)' not found — game version mismatch?");
    }

    [PatchPrefix]
    private static bool Prefix(Class1730 __instance, Weapon weapon, ref bool __result)
    {
        try
        {
            return TrySoftcoreReload(__instance, weapon, ref __result);
        }
        catch (Exception ex)
        {
            // 此处尚未证明存在可用散装弹药，不触发用户通知；安全回退原版。
            Plugin.Log.LogError($"[DirectReload] Reload prefix failed before takeover; falling back to vanilla: {ex}");
            return true;
        }
    }

    /// <returns>true = 执行原版 method_17；false = 本 mod 已接管 __result。</returns>
    private static bool TrySoftcoreReload(Class1730 instance, Weapon weapon, ref bool __result)
    {
        if (!Plugin.EnableDirectLoad.Value)
        {
            Dbg("exit: feature disabled");
            return true;
        }

        if (weapon == null)
        {
            Dbg("exit: weapon null");
            return true;
        }

        if (weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine
            && weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
        {
            Dbg($"exit: reload mode={weapon.ReloadMode} (not ExternalMagazine / EXWIRS)");
            return true;
        }

        var controller = instance.InventoryController_0;
        var magazine = weapon.GetCurrentMagazine();
        if (controller == null)
        {
            Dbg("exit: controller null");
            return true;
        }

        if (magazine == null)
        {
            Dbg("exit: magazine null");
            return true;
        }

        Dbg($"entered: mag={magazine.Count}/{magazine.MaxCount}, chamber={weapon.ChamberAmmoCount}");

        if (magazine.Count >= magazine.MaxCount && weapon.ChamberAmmoCount > 0)
        {
            Dbg("exit: magazine and chamber already full");
            __result = true;
            return false;
        }

        // 保持游戏原版的可触及范围，不递归扩大到背包/弹药盒。
        var ammoPiles = new List<AmmoItemClass>();
        controller.GetReachableItemsOfTypeNonAlloc(ammoPiles, ammo =>
            ammo.StackObjectsCount > 0
            && magazine.CheckCompatibility(ammo)
            && controller.Examined(ammo));

        var reachableRounds = CountLiveRounds(ammoPiles);
        Dbg($"reachable compatible ammo: piles={ammoPiles.Count}, rounds={reachableRounds}");
        if (reachableRounds <= 0)
        {
            return true; // 没有理论上可直装的弹药，不显示本 mod 的失败通知。
        }

        try
        {
            // 原版 ReloadMag 在已有动作时只通过 callback 返回失败。先挡住，避免动作锁定时继续。
            if (!instance.IfirearmHandsController_0.CanStartReload())
            {
                FailTakeover(
                    ref __result,
                    "Cannot start a reload right now",
                    "当前状态无法开始换弹",
                    "CanStartReload=false");
                return false;
            }

            // 使用可被 BeltSlot / PackNStrap Harmony 扩展的容器优先级 API。
            // 旧实现调用 GetPrioritizedGridsForUnloadedObject，只认识原版 TacticalVest + Pockets。
            var freeAddress = FindFreeAddress(controller, magazine);
            Dbg($"temporary magazine address: {(freeAddress == null ? "NULL" : "found")}");

            if (freeAddress == null)
            {
                FailTakeover(
                    ref __result,
                    "No free slot to stow the current magazine — reload not performed",
                    "弹匣没有可用的暂存格，本次装填未执行",
                    "Free up a pocket/rig slot for the current magazine");
                return false;
            }

            // 不再预装子弹：空弹匣也直接进入原版换弹动画，弹数只在动画结束后变化。
            // 原版流程只在弹匣非空时推弹入膛，空弹匣的补上膛在回调里完成（AmmoCommit.ChamberRound）。
            Dbg($"calling ReloadMag (mag={magazine.Count}/{magazine.MaxCount})");
            instance.IfirearmHandsController_0.ReloadMag(
                magazine,
                freeAddress,
                (Callback)delegate(IResult reloadResult)
                {
                    HandleReloadFinished(weapon, controller, magazine, reloadResult);
                });

            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            // 已经证实可触及位置有兼容弹药，符合用户指定的通知触发条件。
            FailTakeover(ref __result, "Direct load flow error", "直接装填流程异常", ex.ToString());
            return false;
        }
    }

    /// <summary>
    /// 动画完成后的收尾（同步回调，与 v1.0.0 行为一致）：
    /// 通过共享提交逻辑补填弹匣并补膛 —— 本地提交（Fika 会话下由宿主镜像对副本执行同一组提交）。
    /// </summary>
    private static void HandleReloadFinished(
        Weapon weapon,
        InventoryController controller,
        MagazineItemClass magazine,
        IResult? reloadResult)
    {
        try
        {
            if (reloadResult == null || reloadResult.Failed)
            {
                var detail = reloadResult?.Error?.ToString() ?? "ReloadMag callback returned null";
                NotifyReloadFailure("Reload transaction failed", "换弹事务失败", detail);
                return;
            }

            var result = AmmoCommit.CommitAfterReload(controller, magazine, weapon, "post-animation");
            if (result.Failed)
            {
                NotifyReloadFailure("Direct load after the animation failed", "动画完成后的装填失败", result.Error);
            }
        }
        catch (Exception ex)
        {
            NotifyReloadFailure("Reload callback error", "换弹回调异常", ex.ToString());
        }
    }

    /// <summary>
    /// 查找安全的临时弹匣落点。GetPrioritizedGridsForLoot 会间接调用
    /// GetPrioritizedContainersForLoot，因此能接受 BeltSlot/PackNStrap 对腰带容器的扩展。
    /// </summary>
    private static GClass3393? FindFreeAddress(InventoryController controller, MagazineItemClass magazine)
    {
        var equipment = controller.Inventory.Equipment;
        var candidateCount = 0;

        foreach (var grid in GClass3372.GetPrioritizedGridsForLoot(equipment, magazine))
        {
            candidateCount++;
            var compatible = grid.CheckCompatibility(magazine);
            var location = compatible ? grid.FindFreeSpace(magazine) : null;

            Dbg(
                $"drop grid[{candidateCount}]: parentType={grid.ParentItem?.GetType().Name ?? "null"}, " +
                $"template={grid.ParentItem?.TemplateId ?? "null"}, grid={grid.ID}, " +
                $"size={grid.GridWidth}x{grid.GridHeight}, compatible={compatible}, free={location != null}");

            if (location != null)
            {
                return grid.CreateItemAddress(location);
            }
        }

        Dbg($"drop-off candidate grids checked: {candidateCount}");
        return null;
    }

    internal static int CountLiveRounds(List<AmmoItemClass> piles)
    {
        var result = 0;
        foreach (var ammo in piles)
        {
            if (ammo != null && ammo.StackObjectsCount > 0 && ammo.CurrentAddress != null)
            {
                result += ammo.StackObjectsCount;
            }
        }

        return result;
    }

    internal static void LogDirectLoad(string stage, MagazineItemClass magazine, AmmoCommit.DirectLoadResult result)
    {
        Dbg(
            $"{stage}: requested={result.Requested}, available={result.Available}, " +
            $"loaded={result.Loaded}, mag={magazine.Count}/{magazine.MaxCount}, error={result.Error ?? "none"}");
    }

    private static void FailTakeover(ref bool result, string reasonEn, string reasonZh, string? detail = null)
    {
        result = true; // 消费本次 method_17，避免原版继续触发不相关的 method_12。
        NotifyReloadFailure(reasonEn, reasonZh, detail);
    }

    /// <summary>
    /// 仅在调用方已经确认「可触及位置存在兼容弹药」之后调用。
    /// DisplaySingletonWarningNotification 是塔科夫右下角原生警告，连续失败不会刷屏堆叠。
    /// 通知文本按游戏界面语言自动本地化（中文界面 → 中文；其他 → 英文）。
    /// </summary>
    internal static void NotifyReloadFailure(string reasonEn, string reasonZh, string? detail = null)
    {
        Plugin.Log.LogWarning(
            $"[DirectReload] Direct reload failed: {reasonEn}" +
            (string.IsNullOrEmpty(detail) ? string.Empty : $" | {detail}"));

        if (!Plugin.EnableReloadFailureNotification.Value)
        {
            return;
        }

        try
        {
            var message = IsChineseGameLanguage()
                ? $"装填失败：{reasonZh}"
                : $"Reload failed: {reasonEn}";
            NotificationManagerClass.DisplaySingletonWarningNotification(message);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] Failed to display reload notification: {ex}");
        }
    }

    /// <summary>
    /// 游戏界面语言是否为中文（"ch"）。读取失败时回退英文。
    /// 语言来源：SharedGameSettingsClass.Game.Settings.Language（GameSetting&lt;string&gt;）。
    /// </summary>
    private static bool IsChineseGameLanguage()
    {
        try
        {
            var language = Singleton<SharedGameSettingsClass>.Instance?.Game?.Settings?.Language?.Value;
            return string.Equals(language, "ch", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void Dbg(string message)
    {
        if (Plugin.DebugLog.Value)
        {
            Plugin.Log.LogInfo($"[DirectReload] DBG: {message}");
        }
    }
}
