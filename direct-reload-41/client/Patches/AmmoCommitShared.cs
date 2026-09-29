using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT.InventoryLogic;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 装填 / 补膛共享提交逻辑 — SPT 4.1 版（强类型）。
/// 与 4.0 版一致：Fika 的物品 ID 序列在客户端与宿主之间锁步同步，
/// 双方各自执行同一组游戏原生原语（同官方 CommitReloadWithAmmo 的 LoadAmmo / PopTo），
/// 即得到一致的物品与数量。绝不要改走网络操作通道（作用于在手上武器弹匣的库存操作会被拒绝）。
/// 弹药堆按物品 ID 排序，保证两端选取顺序完全确定。
/// </summary>
internal static class AmmoCommit
{
    internal readonly struct DirectLoadResult
    {
        public DirectLoadResult(int requested, int available, int loaded, string? error)
        {
            Requested = requested;
            Available = available;
            Loaded = loaded;
            Error = error;
        }

        public int Requested { get; }
        public int Available { get; }
        public int Loaded { get; }
        public string? Error { get; }
        public bool Failed => Error != null;
    }

    /// <summary>可触及的兼容弹药堆（原生同源：FastAccessSlots），按物品 ID 排序保证跨端确定性。</summary>
    internal static List<Ammo> FindReachablePiles(InventoryController controller, Magazine magazine)
    {
        var piles = new List<Ammo>();
        controller.GetReachableItemsOfTypeNonAlloc(
            piles,
            ammo => ammo.StackObjectsCount > 0
                    && magazine.CheckCompatibility(ammo)
                    && controller.Examined(ammo));

        piles.Sort((a, b) => string.CompareOrdinal(a.Id.ToString(), b.Id.ToString()));
        return piles;
    }

    internal static int CountLiveRounds(List<Ammo> piles)
    {
        var total = 0;
        foreach (var ammo in piles)
        {
            if (ammo != null && ammo.StackObjectsCount > 0 && ammo.CurrentAddress != null)
            {
                total += ammo.StackObjectsCount;
            }
        }

        return total;
    }

    /// <summary>复刻原版 CommitReloadWithAmmo：逐发 LoadAmmo 后 RaiseEvents(Begin/Succeed)。两端各自本地执行。</summary>
    internal static DirectLoadResult FillMagazine(
        InventoryController controller,
        Magazine magazine,
        List<Ammo> sourcePiles,
        int maxLoad)
    {
        var livePiles = new List<Ammo>();
        var available = 0;
        foreach (var ammo in sourcePiles)
        {
            if (ammo == null || ammo.StackObjectsCount <= 0 || ammo.CurrentAddress == null)
            {
                continue;
            }

            livePiles.Add(ammo);
            available += ammo.StackObjectsCount;
        }

        var space = Math.Max(0, magazine.MaxCount - magazine.Count);
        var requested = Math.Min(space, maxLoad);
        if (requested <= 0 || available <= 0)
        {
            return new DirectLoadResult(requested, available, 0, null);
        }

        var beforeAll = magazine.Count;
        var ammoPack = new AmmoPack(livePiles);

        for (var i = 0; i < requested && ammoPack.AmmoCount > 0; i++)
        {
            var beforeStep = magazine.Count;
            var loadOp = ammoPack.LoadAmmo(
                controller,
                controller,
                magazine.Cartridges.CreateItemAddress());

            if (loadOp.Error != null)
            {
                return new DirectLoadResult(
                    requested,
                    available,
                    magazine.Count - beforeAll,
                    $"LoadAmmo step {i} failed: {loadOp.Error}");
            }

            loadOp.Value.RaiseEvents(controller, CommandStatus.Begin);
            loadOp.Value.RaiseEvents(controller, CommandStatus.Succeed);

            if (magazine.Count <= beforeStep)
            {
                return new DirectLoadResult(
                    requested,
                    available,
                    magazine.Count - beforeAll,
                    $"LoadAmmo step {i} emitted events but magazine count stayed at {magazine.Count}");
            }
        }

        return new DirectLoadResult(requested, available, magazine.Count - beforeAll, null);
    }

    /// <summary>动画后补上膛（复刻原版空弹匣路径）：弹匣 -1、膛内 +1。两端各自本地执行。</summary>
    internal static void ChamberRound(Weapon weapon, InventoryController controller, Magazine magazine)
    {
        try
        {
            if (!weapon.HasChambers)
            {
                return;
            }

            var chamber = weapon.Chambers[0];
            if (chamber == null || chamber.ContainedItem != null || magazine.Count <= 0)
            {
                return;
            }

            var pop = magazine.Cartridges.PopTo(controller, chamber.CreateItemAddress());
            if (pop.Failed)
            {
                SoftcoreReloadPatch.Dbg($"chamber round failed: {pop.Error}");
                return;
            }

            pop.Value.RaiseEvents(controller, CommandStatus.Begin);
            pop.Value.RaiseEvents(controller, CommandStatus.Succeed);

            SoftcoreReloadPatch.Dbg(
                $"chambered after reload: mag={magazine.Count}/{magazine.MaxCount}, chamber={weapon.ChamberAmmoCount}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] Post-reload chambering failed: {ex}");
        }
    }

    /// <summary>重新取堆 → 填满弹匣 → 补膛（客户端回调与 Fika 宿主镜像共用）。</summary>
    internal static DirectLoadResult CommitAfterReload(
        InventoryController controller,
        Magazine magazine,
        Weapon weapon,
        string stage)
    {
        var piles = FindReachablePiles(controller, magazine);
        var result = FillMagazine(controller, magazine, piles, int.MaxValue);
        SoftcoreReloadPatch.LogDirectLoad(stage, magazine, result);
        if (result.Failed)
        {
            return result;
        }

        ChamberRound(weapon, controller, magazine);
        return result;
    }
}
