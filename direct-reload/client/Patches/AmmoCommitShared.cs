using System;
using System.Collections.Generic;
using EFT.InventoryLogic;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 装填 / 补膛的共享提交逻辑（客户端回调 与 Fika 宿主镜像 两边共用）。
///
/// 设计约束（v1.0.2 教训）：Fika 的物品 ID 序列在客户端与宿主之间锁步同步
/// （连接时交换 CurrentId / NextOperationId），双方各自执行同一组游戏原生原语
/// （与官方 CommitReloadWithAmmo 相同的 LoadAmmo / PopTo），即可得到一致的物品与数量 ——
/// 这是 Fika 对"装填"类变更的原生并行提交模式。
///
/// 注意：绝不要把这里的提交改走网络操作通道（TryRunNetworkTransaction 等）——
/// 作用于"在手上武器弹匣"的库存操作会被游戏的武器操作校验器拒绝
/// （"Can't perform operation ... while mag in the weapon"），而宿主的应用路径不经过该校验，
/// 结果是一侧成功一侧失败、状态撕裂。
///
/// 弹药堆选取顺序按物品 ID 排序，保证两端完全确定、可复现。
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

    /// <summary>
    /// 可触及的兼容弹药堆（与原生同源：FastAccessSlots = 口袋 + 弹挂），按物品 ID 排序保证确定性。
    /// </summary>
    internal static List<AmmoItemClass> FindReachablePiles(InventoryController controller, MagazineItemClass magazine)
    {
        var piles = new List<AmmoItemClass>();
        controller.GetReachableItemsOfTypeNonAlloc(piles, ammo =>
            ammo.StackObjectsCount > 0
            && magazine.CheckCompatibility(ammo)
            && controller.Examined(ammo));

        // 排序保证客户端与宿主的选取顺序一致（两侧物品 ID 锁步，排序结果相同）。
        piles.Sort((a, b) => string.CompareOrdinal(a.Id.ToString(), b.Id.ToString()));
        return piles;
    }

    /// <summary>
    /// 复刻原版 CommitReloadWithAmmo：逐发 LoadAmmo 后 RaiseEvents(Begin/Succeed)。
    /// 与原生不同：返回并核验实际装入数量，不把失败伪装成成功。
    /// </summary>
    internal static DirectLoadResult FillMagazine(
        InventoryController controller,
        MagazineItemClass magazine,
        List<AmmoItemClass> sourcePiles,
        int maxLoad)
    {
        var livePiles = new List<AmmoItemClass>();
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
        var ammoPack = new AmmoPackReloadingClass(livePiles);

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

    /// <summary>
    /// 动画结束后补上膛：原版换弹在装填时会从弹匣抽 1 发进膛，空弹匣没有这一步；
    /// 这里以相同方式复刻（弹匣 -1、膛内 +1），HUD 显示公式与游戏一致（弹匣数 + 膛内 1 发）。
    /// </summary>
    internal static void ChamberRound(Weapon weapon, InventoryController controller, MagazineItemClass magazine)
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

            // v1.2.5 实验：去掉动画参数写入（原 SetAmmoOnMag / SetAmmoInChamber），
            // 仅保留库存层补膛。用于定位「换弹收尾后空转卡死」的成因。

            SoftcoreReloadPatch.Dbg(
                $"chambered after reload: mag={magazine.Count}/{magazine.MaxCount}, chamber={weapon.ChamberAmmoCount}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] Post-reload chambering failed: {ex}");
        }
    }

    /// <summary>
    /// 重新取堆 → 填满弹匣 → 补膛。客户端回调与 Fika 宿主镜像共用这一条路径，
    /// 保证两侧的步骤、数量与物品铸出顺序完全一致。
    /// </summary>
    internal static DirectLoadResult CommitAfterReload(
        InventoryController controller,
        MagazineItemClass magazine,
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
