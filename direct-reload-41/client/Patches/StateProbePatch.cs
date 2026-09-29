using System;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches
{
    /// <summary>
    /// 4.1 诊断辅助：钩住 SearchController.GetObserverItemState，打印每一次观察状态判定。
    /// 用于定位换弹事务内部哪一次物品校验被判为 Unknown / NonExistent。
    /// 仅在 DebugLog 开启时输出。此外提供 StateIdentityProbe（手动调用）。
    /// </summary>
    internal class StateProbePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod()
        {
            return AccessTools.Method(typeof(SearchController), nameof(SearchController.GetObserverItemState));
        }

        [PatchPostfix]
        private static void Postfix(Item item, ItemAddress address, EObserverItemState __result)
        {
            if (!Plugin.DebugLog.Value)
            {
                return;
            }

            string containerInfo;
            try
            {
                var container = address?.Container;
                containerInfo = container == null
                    ? "null"
                    : $"{container.GetType().Name}({container.ID})";
            }
            catch
            {
                containerInfo = "?";
            }

            Plugin.Log.LogInfo(
                $"[DirectReload] DBG: probe: GetObserverItemState(item={item?.Name}({item?.Id}), " +
                $"addrContainer={containerInfo}) => {__result}");
        }
    }

    internal static class StateIdentityProbe
    {
        /// <summary>打印控制器/玩家/搜索控制器的身份关系与地址链校验（仅调试）。</summary>
        internal static void Run(InventoryController controller, Item item, ItemAddress magazineSlot, ItemAddress freeAddress)
        {
            if (!Plugin.DebugLog.Value)
            {
                return;
            }

            try
            {
                var sc = controller.SearchController;
                Plugin.Log.LogInfo($"[DirectReload] DBG: ident: controller={controller.GetType().FullName}, scType={sc?.GetType().FullName}");

                var owner = magazineSlot?.GetOwnerOrNull();
                Plugin.Log.LogInfo($"[DirectReload] DBG: ident: addrOwner={owner?.GetType().FullName ?? "null"}");

                if (owner != null)
                {
                    var playerProp = owner.GetType().GetProperty("Player", BindingFlags.Public | BindingFlags.Instance);
                    var player = playerProp?.GetValue(owner);
                    object playerSc = null;
                    if (player != null)
                    {
                        var scProp = player.GetType().GetProperty("SearchController", BindingFlags.Public | BindingFlags.Instance);
                        playerSc = scProp?.GetValue(player);
                        Plugin.Log.LogInfo($"[DirectReload] DBG: ident: player={player.GetType().FullName}, " +
                            $"playerSc={playerSc?.GetType().FullName ?? "null"}, " +
                            $"sameAsControllerSc={ReferenceEquals(playerSc, sc)}");
                    }
                    else
                    {
                        Plugin.Log.LogInfo("[DirectReload] DBG: ident: player prop not found on owner");
                    }
                }

                if (sc is SearchController baseSc)
                {
                    if (magazineSlot != null)
                    {
                        var changedSlot = baseSc.TryFindChangedContainer(magazineSlot, out var ch1);
                        Plugin.Log.LogInfo($"[DirectReload] DBG: ident: TryFindChangedContainer(magSlot)={changedSlot}, changed={ch1?.GetType().Name ?? "null"}");
                    }

                    if (freeAddress != null)
                    {
                        var changedFree = baseSc.TryFindChangedContainer(freeAddress, out var ch2);
                        Plugin.Log.LogInfo($"[DirectReload] DBG: ident: TryFindChangedContainer(freeAddr)={changedFree}, changed={ch2?.GetType().Name ?? "null"}");
                    }
                }

                if (freeAddress != null && sc != null)
                {
                    var stateFree = sc.GetObserverItemState(item, freeAddress);
                    var knownFree = sc.IsItemKnown(item, freeAddress);
                    Plugin.Log.LogInfo($"[DirectReload] DBG: ident: stateAtFree={stateFree}, knownAtFree={knownFree}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogInfo($"[DirectReload] DBG: ident probe failed: {ex.Message}");
            }
        }
    }
}
