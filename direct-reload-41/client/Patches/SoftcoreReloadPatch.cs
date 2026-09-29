using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.Settings;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 潜行者式装填 — SPT 4.1 版（强类型）。
/// 目标：EFT.FirearmHandsInputTranslator.ReloadExternalMagazine(Weapon, bool)
///   （4.0: Class1730.method_17 —— 逻辑与 4.0 逐行同构）。
/// 覆盖模式：ExternalMagazine + EXWIRS；纯弹仓武器保持原版。
/// 4.1 编译参考集 = hollowed.dll（与 Fika 等所有 4.1 客户端 mod 相同）。
/// </summary>
internal class SoftcoreReloadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
                   typeof(FirearmHandsInputTranslator),
                   "ReloadExternalMagazine",
                   new[] { typeof(Weapon), typeof(bool) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] FirearmHandsInputTranslator.ReloadExternalMagazine not found — game version mismatch?");
    }

    [PatchPrefix]
    private static bool Prefix(FirearmHandsInputTranslator __instance, Weapon weapon, bool quickReload, ref bool __result)
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

    /// <returns>true = 执行原版；false = 本 mod 已接管 __result。</returns>
    private static bool TrySoftcoreReload(FirearmHandsInputTranslator instance, Weapon weapon, ref bool __result)
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

        var controller = instance._inventoryController;
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

        // 诊断：观察状态链路（0.16 物品观察系统——未观察物品禁止操作）
        if (Plugin.DebugLog.Value)
        {
            try
            {
                var sc = controller.SearchController;
                var magState = sc?.GetObserverItemState(magazine, magazine.Parent);
                var magKnown = sc?.IsItemKnown(magazine, magazine.Parent) ?? false;
                var weaponKnown = weapon.Parent != null && (sc?.IsItemKnown(weapon, weapon.Parent) ?? false);
                var examined = controller.Examined(magazine);
                Plugin.Log.LogInfo(
                    $"[DirectReload] DBG: diag: sc={sc?.GetType().Name ?? "null"}, magState={magState}, magKnown={magKnown}, " +
                    $"weaponKnown={weaponKnown}, magExamined={examined}, " +
                    $"magOwner={magazine.Parent?.GetOwner()?.GetType().Name ?? "?"}");
            }
            catch (Exception dex)
            {
                Plugin.Log.LogInfo($"[DirectReload] DBG: diag failed: {dex.Message}");
            }
        }

        if (magazine.Count >= magazine.MaxCount && weapon.ChamberAmmoCount > 0)
        {
            Dbg("exit: magazine and chamber already full");
            __result = true;
            return false;
        }

        // 可触及的兼容弹药（预检口径与提交一致：同一实现）。
        List<Ammo> piles;
        int reachableRounds;
        try
        {
            piles = AmmoCommit.FindReachablePiles(controller, magazine);
            reachableRounds = AmmoCommit.CountLiveRounds(piles);
        }
        catch (Exception ex)
        {
            Dbg($"exit: reachable ammo scan failed: {ex.Message}");
            return true; // 扫描失败按"无弹药"处理，回退原版且不显示本 mod 通知
        }

        Dbg($"reachable compatible ammo: piles={piles.Count}, rounds={reachableRounds}");
        if (reachableRounds <= 0)
        {
            return true; // 没有理论上可直装的弹药，不显示本 mod 的失败通知。
        }

        try
        {
            var hands = instance._controller;
            if (hands == null)
            {
                Dbg("exit: hands controller unavailable");
                return true;
            }

            // 原版在已有动作时只通过 callback 返回失败。先挡住，避免动作锁定时继续。
            if (!hands.CanStartReload())
            {
                FailTakeover(
                    ref __result,
                    "Cannot start a reload right now",
                    "当前状态无法开始换弹",
                    "CanStartReload=false");
                return false;
            }

            var freeAddress = FindFreeAddress(controller, magazine);
            Dbg($"temporary magazine address: {(freeAddress == null ? "NULL" : "found")}");
            StateIdentityProbe.Run(controller, magazine, magazine.Parent, freeAddress);

            if (freeAddress == null)
            {
                FailTakeover(
                    ref __result,
                    "No free slot to stow the current magazine — reload not performed",
                    "弹匣没有可用的暂存格，本次装填未执行",
                    "Free up a pocket/rig slot for the current magazine");
                return false;
            }

            Dbg($"calling ReloadMag (mag={magazine.Count}/{magazine.MaxCount})");

            // 4.1 观察系统：同一弹匣在事务内二次移动（枪 → 暂存格 → 枪）时，
            // 第二次校验使用"暂存格地址"——该地址从未登记 → IsItemKnown=False → UnknownItemError。
            // 与游戏自身拖拽机制同源：把在途物品标记为"临时已知"，动画回调后移除。
            var activeSearch = controller.SearchController as ActiveSearchController;
            var tempKnownSet = false;
            if (activeSearch != null)
            {
                try
                {
                    activeSearch.SetItemAsTemporaryKnown(magazine);
                    tempKnownSet = true;
                    Dbg("temporary-known set for stowed magazine");
                }
                catch (Exception tex)
                {
                    Dbg($"SetItemAsTemporaryKnown failed: {tex.Message}");
                }
            }
            else
            {
                Dbg($"search controller is {controller.SearchController?.GetType().Name ?? "null"} — temporary-known skipped");
            }

            try
            {
                hands.ReloadMag(
                    magazine,
                    freeAddress,
                    (Callback)delegate(IResult reloadResult)
                    {
                        HandleReloadFinished(weapon, controller, magazine, reloadResult);
                    });
            }
            catch
            {
                if (tempKnownSet)
                {
                    TryRemoveTemporaryKnown(activeSearch, magazine);
                }
                throw;
            }

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

    /// <summary>动画完成后的收尾：共享提交逻辑补填弹匣并补膛（Fika 会话下由宿主镜像执行同一组提交）。</summary>
    private static void HandleReloadFinished(
        Weapon weapon,
        InventoryController controller,
        Magazine magazine,
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
        finally
        {
            TryRemoveTemporaryKnown(controller.SearchController as ActiveSearchController, magazine);
        }
    }

    /// <summary>移除"临时已知"标记（事务/动画结束后调用；未设置时无害）。</summary>
    private static void TryRemoveTemporaryKnown(ActiveSearchController? activeSearch, Magazine magazine)
    {
        if (activeSearch == null)
        {
            return;
        }

        try
        {
            activeSearch.RemoveItemFromTemporaryKnown(magazine);
            Dbg("temporary-known removed");
        }
        catch (Exception ex)
        {
            Dbg($"RemoveItemFromTemporaryKnown failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 4.1 原版同款临时落点搜索（与经典换弹流程完全一致）：
    /// GetPrioritizedGridsForUnloadedObject（仅 弹挂 + 口袋），逐网格 FindLocationForItem(弹匣)，
    /// 取面积最小的非空落点。不含背包 / 安全箱（战中放入安全箱会被拒绝）。
    /// </summary>
    private static GridItemAddress? FindFreeAddress(InventoryController controller, Magazine magazine)
    {
        try
        {
            var equipment = controller.Inventory.Equipment;
            GridItemAddress? best = null;
            var bestArea = int.MaxValue;
            var candidateCount = 0;

            foreach (var grid in InventoryEquipmentExtension.GetPrioritizedGridsForUnloadedObject(equipment))
            {
                candidateCount++;
                var location = grid.FindLocationForItem(magazine);
                if (location == null)
                {
                    continue;
                }

                var area = grid.GridWidth * grid.GridHeight;
                if (Plugin.DebugLog.Value)
                {
                    Plugin.Log.LogInfo(
                        $"[DirectReload] DBG: drop candidate #{candidateCount}: " +
                        $"parent={grid.ParentItem?.Name ?? "?"}, area={area}");
                }

                if (area < bestArea)
                {
                    bestArea = area;
                    best = location;
                }
            }

            Dbg($"drop-off candidates checked: {candidateCount}, picked={best != null}");
            return best;
        }
        catch (Exception ex)
        {
            Dbg($"FindFreeAddress failed: {ex.Message}");
            return null;
        }
    }

    private static void FailTakeover(ref bool result, string reasonEn, string reasonZh, string? detail = null)
    {
        result = true; // 消费本次调用，避免原版继续往下走。
        NotifyReloadFailure(reasonEn, reasonZh, detail);
    }

    /// <summary>
    /// 仅在已确认可触及位置存在兼容弹药之后调用。
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
            NotificationManager.DisplaySingletonWarningNotification(message);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] Failed to display reload notification: {ex}");
        }
    }

    /// <summary>游戏界面语言是否为中文（"ch"）。读取失败时回退英文。</summary>
    private static bool IsChineseGameLanguage()
    {
        try
        {
            var language = Singleton<SettingsManager>.Instance?.Game?.Settings?.Language?.Value;
            return string.Equals(language, "ch", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static void LogDirectLoad(string stage, Magazine magazine, AmmoCommit.DirectLoadResult result)
    {
        Dbg(
            $"{stage}: requested={result.Requested}, available={result.Available}, " +
            $"loaded={result.Loaded}, mag={magazine.Count}/{magazine.MaxCount}, error={result.Error ?? "none"}");
    }

    internal static void Dbg(string message)
    {
        if (Plugin.DebugLog.Value)
        {
            Plugin.Log.LogInfo($"[DirectReload] DBG: {message}");
        }
    }
}
