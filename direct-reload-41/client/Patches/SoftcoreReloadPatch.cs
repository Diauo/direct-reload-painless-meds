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
using RZDirectReload.Client.Fika;
using RZDirectReload.Client.Inputs;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 潜行者式装填 — SPT 4.1 版（强类型）。
/// 目标：EFT.FirearmHandsInputTranslator.ReloadExternalMagazine(Weapon, bool)
///   （4.0: Class1730.method_17 —— 逻辑与 4.0 逐行同构）。
/// 覆盖模式：ExternalMagazine + EXWIRS；纯弹仓武器保持原版。
///
/// 1.3.0 两段式输入：
///   · 轮盘关闭 → 现役行为（按下即装；旧路径保留）。
///   · 轮盘开启 → 按下只挂起；轻按（松开 &lt; 阈值）= 直装；长按（≥ 阈值）= 弹药选择轮盘（只选不装）。
///   · tap 后仍须交原版的情况 → 一次性护栏 + 缓存方法转发（防前缀重入）。
/// </summary>
internal class SoftcoreReloadPatch : ModulePatch
{
    private static MethodBase? _targetMethod;

    /// <summary>转发原版时的一次性护栏（防止 Invoke 重入本前缀形成死循环）。</summary>
    [ThreadStatic]
    private static bool _vanillaForward;

    protected override MethodBase GetTargetMethod()
    {
        _targetMethod = AccessTools.Method(
                   typeof(FirearmHandsInputTranslator),
                   "ReloadExternalMagazine",
                   new[] { typeof(Weapon), typeof(bool) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] FirearmHandsInputTranslator.ReloadExternalMagazine not found — game version mismatch?");
        return _targetMethod;
    }

    private static bool _cantRemoveResolved;
    private static MethodInfo? _cantRemoveGetter;

    /// <summary>
    /// 直装适用性判定（与游戏自身的战局内移动规则同一把尺，ItemManipulator.MovePathCheck）：
    ///   ① 武器必须是外置弹匣类（ExternalMagazine / EXWIRS）；
    ///   ② 弹匣所在槽位不是"关键部件"（Required）——内置管仓（Marlin M1894 等）在战局内不可拆；
    ///   ③ 弹匣本体可战局内拆卸（RaidModdable）——转轮（RSH-12 等）不可；
    ///   ④ 无"战局内禁止从槽位移除"组件（反射解析一次后缓存——每换弹调用零反射开销）。
    /// 不适用 → 保持原版逐发装填（绝不执行直装的"弹匣拆装舞蹈"）。
    /// </summary>
    internal static bool IsDirectLoadCapable(Weapon weapon)
    {
        try
        {
            if (weapon == null)
            {
                return false;
            }

            if (weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazine
                && weapon.ReloadMode != Weapon.EReloadMode.ExternalMagazineWithInternalReloadSupport)
            {
                return false;
            }

            var magazine = weapon.GetCurrentMagazine();
            if (magazine == null)
            {
                return false;
            }

            if (magazine.Parent?.Container is Slot slot && slot.Required)
            {
                return false;
            }

            if (magazine is Mod { RaidModdable: false })
            {
                return false;
            }

            // ④ 战局内禁止从槽位移除的组件（运行时反射检查——该组件类型位于拆分程序集，编译期不引用；
            //    解析一次后缓存，避免每次换弹都做类型扫描）
            if (!_cantRemoveResolved)
            {
                _cantRemoveResolved = true;
                try
                {
                    var componentType = AccessTools.TypeByName("EFT.InventoryLogic.CantRemoveFromSlotsDuringRaidComponent");
                    var getter = typeof(Item)
                        .GetMethods()
                        .FirstOrDefault(m => m.Name == "GetItemComponent" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    _cantRemoveGetter = componentType != null && getter != null
                        ? getter.MakeGenericMethod(componentType)
                        : null;
                }
                catch
                {
                    _cantRemoveGetter = null;
                }
            }

            if (_cantRemoveGetter != null)
            {
                try
                {
                    if (_cantRemoveGetter.Invoke(magazine, null) != null)
                    {
                        return false;
                    }
                }
                catch
                {
                    // 反射失败按"无组件"处理（保守方向：不因反射问题误伤直装）
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            Dbg($"capability check failed ({ex.Message}) → vanilla");
            return false;
        }
    }

    [PatchPrefix]
    private static bool Prefix(FirearmHandsInputTranslator __instance, Weapon weapon, bool quickReload, ref bool __result)
    {
        if (_vanillaForward)
        {
            return true; // 原版转发中：直接放行原版体
        }

        try
        {
            // ── 是否进入本系统的最早闸门（判定失败即回原版） ──
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

            if (!IsDirectLoadCapable(weapon))
            {
                Dbg($"exit: not direct-load capable (mode={weapon.ReloadMode}) → vanilla");
                return true;
            }

            if (!Plugin.EnableAmmoWheel.Value)
            {
                // 轮盘关闭：保持现役「按下即装」行为（旧路径一字不改）
                Dbg("wheel path: disabled → legacy");
                return TrySoftcoreReload(__instance, weapon, ref __result);
            }

            if (__instance._inventoryController == null)
            {
                Dbg("exit: controller null");
                return true;
            }

            if (weapon.GetCurrentMagazine() == null)
            {
                Dbg("exit: magazine null");
                return true;
            }

            // 轮盘模式：消费命令并记录（按键手势由物理键监视器驱动——与绑定触发类型无关：
            // Press 触发在按下时到达、Release 触发在松开时到达，监视器都能正确识别手势）
            AmmoWheelController.NoteCommand(__instance, weapon);
            Dbg("wheel: command consumed (gesture watcher drives)");
            __result = true;
            return false;
        }
        catch (Exception ex)
        {
            // 此处尚未证明存在可用散装弹药，不触发用户通知；安全回退原版。
            Plugin.Log.LogError($"[DirectReload] Reload prefix failed before takeover; falling back to vanilla: {ex}");
            return true;
        }
    }

    /// <summary>
    /// 轻按路径（以及轮盘关闭时的现役路径）：直装全流程。
    /// </summary>
    /// <returns>true = 应交原版执行（调用方负责转发）；false = 本 mod 已接管 __result。</returns>
    internal static bool TrySoftcoreReload(FirearmHandsInputTranslator instance, Weapon weapon, ref bool __result)
    {
        if (!Plugin.EnableDirectLoad.Value)
        {
            Dbg("exit: feature disabled");
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

        if (!IsDirectLoadCapable(weapon))
        {
            Dbg("exit: not direct-load capable (guard) → vanilla");
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

        // 可触及的兼容弹药（预检口径与提交一致：同一实现）。
        List<Ammo> piles;
        int reachableRounds;
        try
        {
            piles = AmmoCommit.FindReachablePiles(controller, magazine, weapon);
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

            // 捕获本轮的弹药选择（提交与 Fika 同步共用同一份快照，杜绝竞态）
            var capturedSelection = AmmoSelectionState.Current.Snapshot();

            // 通知宿主镜像（单机 / 无 Fika 时为空操作；在 ReloadMag 之前发送，同通道有序）
            try
            {
                FikaAmmoSelectionBridge.SendSelection(controller.ID, capturedSelection);
            }
            catch (Exception sex)
            {
                Dbg($"selection send failed: {sex.Message}");
            }

            // 空仓 + 空匣的直装：原版换弹准备阶段只在"新弹匣有弹"时才规划上膛操作
            // （ReloadExternalMagResult.PopNewAmmoResult）——否则换弹动画没有拉机柄段。
            // 我们的直装复用同一弹匣，动画开始时它是空的，原版便不会规划上膛；
            // 因此在开始动画前先向弹匣顶部预置 1 发（选中弹药），让原版自己完成上膛与拉机柄动画。
            if (weapon.ChamberAmmoCount == 0 && magazine.Count == 0)
            {
                PreloadSingleRoundForChamber(controller, magazine, capturedSelection, piles);
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
                        HandleReloadFinished(weapon, controller, magazine, reloadResult, capturedSelection);
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

    /// <summary>
    /// 空仓空匣直装前预置 1 发到弹匣顶部（选中弹药）：让原版换弹准备阶段规划"上膛"操作，
    /// 换弹动画因此自带拉机柄段；该发会在动画中由游戏自己上膛（随后我们的提交补满弹匣）。
    /// 失败静默（最多退化为无拉机柄动画，不影响装填本身）。
    /// </summary>
    private static void PreloadSingleRoundForChamber(
        InventoryController controller,
        Magazine magazine,
        AmmoSelectionSnapshot selection,
        List<Ammo> piles)
    {
        try
        {
            var ordered = AmmoCommit.OrderPiles(piles, selection);
            var pile = ordered.FirstOrDefault(a => a != null && a.StackObjectsCount > 0 && a.CurrentAddress != null);
            if (pile == null)
            {
                return;
            }

            var load = ItemManipulator.ApplySingleItemToAddress(
                pile,
                controller,
                controller,
                magazine.Cartridges.CreateItemAddress());
            if (load.Failed)
            {
                Dbg($"preload chamber round failed: {load.Error}");
                return;
            }

            load.Value.RaiseEvents(controller, CommandStatus.Begin);
            load.Value.RaiseEvents(controller, CommandStatus.Succeed);
            Dbg($"preloaded 1 round for chamber animation (template={pile.TemplateId})");
        }
        catch (Exception ex)
        {
            Dbg($"preload chamber round error: {ex.Message}");
        }
    }

    /// <summary>转发原版（tap 后仍须交原版的情况：如无弹药 → 原版备用弹匣流程）。</summary>
    internal static void ForwardToVanilla(FirearmHandsInputTranslator instance, Weapon weapon, bool quickReload)
    {
        var target = _targetMethod;
        if (target == null || instance == null || weapon == null)
        {
            return;
        }

        _vanillaForward = true;
        try
        {
            Dbg("forwarding reload to vanilla");
            target.Invoke(instance, new object[] { weapon, quickReload });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] vanilla reload forward failed: {ex}");
        }
        finally
        {
            _vanillaForward = false;
        }
    }

    /// <summary>动画完成后的收尾：共享提交逻辑补填弹匣并补膛（Fika 会话下由宿主镜像执行同一组提交）。</summary>
    private static void HandleReloadFinished(
        Weapon weapon,
        InventoryController controller,
        Magazine magazine,
        IResult? reloadResult,
        AmmoSelectionSnapshot capturedSelection)
    {
        try
        {
            if (reloadResult == null || reloadResult.Failed)
            {
                var detail = reloadResult?.Error?.ToString() ?? "ReloadMag callback returned null";
                NotifyReloadFailure("Reload transaction failed", "换弹事务失败", detail);
                return;
            }

            var createdIds = new List<string>();
            var result = AmmoCommit.CommitAfterReload(controller, magazine, weapon, "post-animation", capturedSelection, createdIds);
            if (result.Failed)
            {
                NotifyReloadFailure("Direct load after the animation failed", "动画完成后的装填失败", result.Error);
            }
            else
            {
                // 每次成功提交都必须通知宿主：版本化信封；整堆移动可能零新 ID——空清单也要发。
                try
                {
                    FikaAmmoSelectionBridge.SendIds(controller.ID, createdIds);
                }
                catch (Exception sex)
                {
                    Dbg($"ids send failed: {sex.Message}");
                }
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
