using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
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

    /// <summary>可触及的兼容弹药堆（原生同源 FastAccessSlots + 腰带所在的手环槽），按物品 ID 排序保证跨端确定性。
    /// 用 GetAcceptableItemsNonAlloc 显式传槽位——不要用 GetReachableItemsOfTypeNonAlloc：
    /// 后者只认原版 FastAccessSlots = 口袋+弹挂，腰带（ArmBand 槽）搜不到，
    /// 且 UseItemsAnywhere 只改写游戏本体调用点、覆盖不到本 mod 自己的查询。
    /// 兼容链 = 弹匣可装 ∧ 武器枪膛可接纳（排除"弹匣能装但枪打不了"的弹药，如 5.56 枪 + .300 弹）。</summary>
    internal static List<Ammo> FindReachablePiles(InventoryController controller, Magazine magazine, Weapon? weapon = null)
    {
        var piles = new List<Ammo>();
        controller.GetAcceptableItemsNonAlloc(
            BuildReachableSlots(controller),
            piles,
            ammo => ammo.StackObjectsCount > 0
                    && controller.Examined(ammo)
                    && ammo.CheckAction(null).Succeeded
                    && magazine.CheckCompatibility(ammo)
                    && WeaponAccepts(weapon, ammo),
            null);

        piles.Sort((a, b) => string.CompareOrdinal(a.Id.ToString(), b.Id.ToString()));
        return piles;
    }

    /// <summary>可触及槽位 = 游戏原生 FastAccessSlots（若 PackNStrap 等模组替换了该静态字段则自然继承）
    /// + 手环槽 ArmBand（BeltSlot 腰带占用此槽）。只保留当前装备上确实存在的槽位，
    /// 避免 modded/bot 装备槽位缓存不完整导致的越界/空引用。</summary>
    private static EquipmentSlot[] BuildReachableSlots(InventoryController controller)
    {
        var wanted = new List<EquipmentSlot>();
        var baseSlots = Inventory.FastAccessSlots;
        if (baseSlots != null)
        {
            foreach (var slot in baseSlots)
            {
                if (!wanted.Contains(slot))
                {
                    wanted.Add(slot);
                }
            }
        }

        if (!wanted.Contains(EquipmentSlot.ArmBand))
        {
            wanted.Add(EquipmentSlot.ArmBand);
        }

        var equipment = controller.Inventory.Equipment;
        var reachable = new List<EquipmentSlot>(wanted.Count);
        foreach (var slot in wanted)
        {
            try
            {
                if (equipment.GetSlot(slot) != null)
                {
                    reachable.Add(slot);
                }
            }
            catch
            {
                // 当前装备无此槽（modded/bot 装备缓存更短）→ 跳过
            }
        }

        return reachable.ToArray();
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

    /// <summary>复刻原版 CommitReloadWithAmmo：逐发 LoadAmmo 后 RaiseEvents(Begin/Succeed)。两端各自本地执行（ID 生成器可注入对齐）。</summary>
    internal static DirectLoadResult FillMagazine(
        InventoryController controller,
        Magazine magazine,
        List<Ammo> sourcePiles,
        int maxLoad,
        IDatabaseIdGenerator? idGenerator = null)
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

        // 1) 按优先级计算取量计划：目标弹药优先取用，不足由后续堆依序补足——
        //    内容（各弹种数量）与顺序语义解耦，总量与“先装目标弹”方案完全一致。
        var plan = new List<(Ammo Pile, int Take)>();
        var remaining = requested;
        foreach (var ammo in livePiles)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(remaining, ammo.StackObjectsCount);
            if (take > 0)
            {
                plan.Add((ammo, take));
                remaining -= take;
            }
        }

        // 2) 反向执行装填（低优先级先入匣 = 堆底部；目标弹药最后入匣 = 堆顶部）。
        //    塔科夫弹匣从顶部（Cartridges.Last）消耗——上膛与射击都取 Last——
        //    因此只有反向装填才能让“优先弹药先打、打完降级”，与选择语义一致。
        //    批量执行：同型顶部弹堆可合时 Merge/TransferExact 整段转移，否则整堆 Move /
        //    SplitExact 分段——一次原生操作代替逐发操作（30 发从 30 op/60 事件 → 1 op/2 事件）。
        var step = 0;
        for (var p = plan.Count - 1; p >= 0; p--)
        {
            var (pile, take) = plan[p];
            var remainingTake = take;
            while (remainingTake > 0)
            {
                var beforeStep = magazine.Count;
                var loadOp = LoadStack(controller, magazine, pile, remainingTake, idGenerator ?? controller, out var loadError, out var moved);

                if (loadOp == null || loadError != null)
                {
                    return new DirectLoadResult(
                        requested,
                        available,
                        magazine.Count - beforeAll,
                        $"LoadStack step {step} failed: {loadError ?? "unknown error"}");
                }

                loadOp.RaiseEvents(controller, CommandStatus.Begin);
                loadOp.RaiseEvents(controller, CommandStatus.Succeed);

                if (moved <= 0 || magazine.Count - beforeStep != moved)
                {
                    return new DirectLoadResult(
                        requested,
                        available,
                        magazine.Count - beforeAll,
                        $"LoadStack step {step} expected {moved} rounds but magazine changed by {magazine.Count - beforeStep}");
                }

                step++;
                remainingTake -= moved;
            }
        }

        return new DirectLoadResult(requested, available, magazine.Count - beforeAll, null);
    }

    /// <summary>
    /// 单次批量装填（采纳社区维护包设计）：同型（模板 + 已使用态一致）顶部弹堆可合时
    /// Merge（整堆）/ TransferExact（分段），否则整堆 Move（**不产生新 ID**）/ SplitExact 分段。
    /// 与游戏自身 ApplySingleItemToAddress 使用同一批原生原语；本地执行（不走操作通道）。
    /// </summary>
    private static IItemOperationResult? LoadStack(
        InventoryController controller,
        Magazine magazine,
        Ammo pile,
        int count,
        IDatabaseIdGenerator idGenerator,
        out string? error,
        out int moved)
    {
        moved = count;
        error = null;

        var top = magazine.Cartridges.Last as Ammo;
        if (top != null && top.TemplateId == pile.TemplateId && top.IsUsed == pile.IsUsed)
        {
            var mergeCount = Math.Min(count, Math.Max(0, top.StackMaxSize - top.StackObjectsCount));
            if (mergeCount > 0)
            {
                // 与 ApplySingleItemToAddress 同批原语，但按整堆数量转移。
                if (mergeCount == pile.StackObjectsCount)
                {
                    var merge = ItemManipulator.Merge(pile, top, controller, simulate: false);
                    if (merge.Succeeded)
                    {
                        moved = mergeCount;
                        return merge.Value;
                    }
                }
                else
                {
                    var transfer = ItemManipulator.TransferExact(pile, mergeCount, top, controller, simulate: false);
                    if (transfer.Succeeded)
                    {
                        moved = mergeCount;
                        return transfer.Value;
                    }
                }

                // 原生合并/转移失败会自动回滚；保留与原版一致的 add 回退路径。
            }
        }

        var address = magazine.Cartridges.CreateItemAddress();
        if (count == pile.StackObjectsCount)
        {
            var move = ItemManipulator.Move(pile, address, controller);
            if (move.Succeeded)
            {
                return move.Value;
            }

            error = move.Error?.ToString();
            return null;
        }

        var split = ItemManipulator.SplitExact(pile, count, address, controller, idGenerator, simulate: false);
        if (split.Succeeded)
        {
            return split.Value;
        }

        error = split.Error?.ToString();
        return null;
    }

    /// <summary>
    /// 卸空弹匣：把弹匣内所有弹药堆依序移回装备容器（口袋/弹挂/背包）。
    /// 本地原语执行（simulate:false 直接移动 + RaiseEvents），指令序确定（始终取栈末、
    /// 按目标容器顺序搜索）——客户端与 Fika 宿主镜像各自执行同一操作，保证两端一致。
    /// </summary>
    /// <returns>null = 成功；否则为错误信息（已尽力回滚已移动的堆）。</returns>
    internal static string? UnloadMagazine(InventoryController controller, Magazine magazine)
    {
        var moved = new List<IItemOperationResult>();
        var rolledBack = false;
        string? error = null;

        try
        {
            var targets = new CompoundItem[] { controller.Inventory.Equipment };
            var guard = 0;
            while (magazine.Cartridges.Count > 0 && guard++ < 64)
            {
                if (magazine.Cartridges.Last is not Ammo ammo)
                {
                    break;
                }

                // 注意：合并路径会把源弹堆解引用（CurrentAddress=null），
                // 所以只允许在移动“前”读取信息，移动后不得再访问 ammo 的任何属主信息。
                var stackSize = SafeStackCount(ammo);

                var found = ItemManipulator.QuickFindAppropriatePlace(
                    ammo,
                    controller,
                    targets,
                    ItemManipulator.EMoveItemOrder.UnloadAmmo,
                    simulate: false);

                if (found.Failed)
                {
                    // 回滚必须后进先出：正序回滚会把弹堆顺序打乱（影响击发顺序——社区维护包复现 4,5,6 → 4,6,5）。
                    for (var i = moved.Count - 1; i >= 0; i--)
                    {
                        try
                        {
                            moved[i].RollBack();
                        }
                        catch
                        {
                            // ignore
                        }
                    }

                    rolledBack = true;
                    error = found.Error?.ToString() ?? "no place for unloaded ammo";
                    break;
                }

                moved.Add(found.Value);
                SoftcoreReloadPatch.Dbg($"unload: moved a stack of {stackSize}");
            }
        }
        catch (Exception ex)
        {
            // 状态已部分变更：已完成的移动保留，为其补发事件后再报告错误
            error = ex.Message;
        }

        if (!rolledBack && moved.Count > 0)
        {
            try
            {
                foreach (var op in moved)
                {
                    op.RaiseEvents(controller, CommandStatus.Begin);
                    op.RaiseEvents(controller, CommandStatus.Succeed);
                }

                magazine.RaiseRefreshEvent();
            }
            catch (Exception ex)
            {
                SoftcoreReloadPatch.Dbg($"unload: event raise failed: {ex.Message}");
            }
        }

        return error;
    }

    private static int SafeStackCount(Ammo ammo)
    {
        try
        {
            return ammo.StackObjectsCount;
        }
        catch
        {
            return -1;
        }
    }

    // ── ID 对齐（双端物品身份一致） ────────────────────────

    /// <summary>记录型生成器（客户端）：透传真实生成器并记录每个新建物品 ID（供宿主强制对齐）。</summary>
    private sealed class RecordingIdGenerator : IDatabaseIdGenerator
    {
        private readonly IDatabaseIdGenerator _inner;
        private readonly List<string> _sink;

        internal RecordingIdGenerator(IDatabaseIdGenerator inner, List<string> sink)
        {
            _inner = inner;
            _sink = sink;
        }

        public MongoID NextId
        {
            get
            {
                var id = _inner.NextId;
                _sink.Add(id.ToString());
                return id;
            }
        }

        public void RollBack()
        {
            _inner.RollBack();
        }
    }

    /// <summary>强制型生成器（宿主镜像）：按客户端记录的顺序强制生成相同物品 ID；耗尽后回退随机生成并警告。</summary>
    private sealed class ForcedIdGenerator : IDatabaseIdGenerator
    {
        private readonly IReadOnlyList<string> _ids;
        private readonly IDatabaseIdGenerator _inner;
        private bool _exhaustWarned;
        private int _index;

        internal ForcedIdGenerator(IReadOnlyList<string> ids, IDatabaseIdGenerator inner)
        {
            _ids = ids;
            _inner = inner;
        }

        public MongoID NextId
        {
            get
            {
                if (_index < _ids.Count)
                {
                    var raw = _ids[_index++];
                    try
                    {
                        return new MongoID(raw);
                    }
                    catch (Exception ex)
                    {
                        SoftcoreReloadPatch.Dbg($"forced id parse failed ({raw}): {ex.Message}");
                    }
                }

                if (!_exhaustWarned)
                {
                    _exhaustWarned = true;
                    Plugin.Log.LogWarning(
                        "[DirectReload] host mirror: forced id list exhausted — falling back to random ids (possible drift)");
                }

                return _inner.NextId;
            }
        }

        public void RollBack()
        {
            _inner.RollBack();
        }
    }

    /// <summary>动画后补上膛（复刻原版空弹匣路径）：弹匣 -1、膛内 +1。两端各自本地执行（ID 生成器可注入对齐）。</summary>
    internal static void ChamberRound(Weapon weapon, InventoryController controller, Magazine magazine, IDatabaseIdGenerator? idGenerator = null)
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

            if (magazine.Cartridges.Last is not Ammo top || top.StackObjectsCount <= 0)
            {
                return;
            }

            // 与原版 PopTo 等价（内部同批原语），但生成器可注入以强制 ID 对齐；
            // ApplySingleItemToAddress 自动处理"顶部仅 1 发无法 Split"的边界（走 Move）。
            var split = ItemManipulator.ApplySingleItemToAddress(
                top,
                idGenerator ?? controller,
                controller,
                chamber.CreateItemAddress());
            if (split.Failed)
            {
                SoftcoreReloadPatch.Dbg($"chamber round failed: {split.Error}");
                return;
            }

            split.Value.RaiseEvents(controller, CommandStatus.Begin);
            split.Value.RaiseEvents(controller, CommandStatus.Succeed);

            SoftcoreReloadPatch.Dbg(
                $"chambered after reload: mag={magazine.Count}/{magazine.MaxCount}, chamber={weapon.ChamberAmmoCount}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] Post-reload chambering failed: {ex}");
        }
    }

    /// <summary>
    /// 按选择快照排序取弹顺序（双端一致性硬约束——客户端提交与宿主镜像必须执行完全相同的排序）：
    ///   ① Explicit 模式：目标模板优先；
    ///   ② 穿深降序；
    ///   ③ itemId 字符串升序（决定性兜底）。
    /// </summary>
    internal static List<Ammo> OrderPiles(List<Ammo> piles, AmmoSelectionSnapshot selection)
    {
        var ordered = new List<Ammo>(piles);
        bool explicitMode = selection.Mode == AmmoSelectionMode.Explicit
                            && !string.IsNullOrEmpty(selection.TemplateId);

        ordered.Sort((a, b) =>
        {
            if (explicitMode)
            {
                bool aTarget = a.TemplateId.ToString() == selection.TemplateId;
                bool bTarget = b.TemplateId.ToString() == selection.TemplateId;
                if (aTarget != bTarget)
                {
                    return aTarget ? -1 : 1;
                }
            }

            int pen = b.PenetrationPower.CompareTo(a.PenetrationPower);
            if (pen != 0)
            {
                return pen;
            }

            return string.CompareOrdinal(a.Id.ToString(), b.Id.ToString());
        });

        return ordered;
    }

    /// <summary>武器-弹药兼容（与官方装填预检一致：以枪膛口径为准）。异常时按允许处理（保持原有行为）。</summary>
    internal static bool WeaponAccepts(Weapon? weapon, Ammo ammo)
    {
        try
        {
            return weapon == null
                || !weapon.HasChambers
                || weapon.Chambers.Length == 0
                || weapon.Chambers[0].CanAccept(ammo);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 重新取堆 → 按选择排序 → 填满弹匣 → 补膛 → 记录最后使用（客户端回调与 Fika 宿主镜像共用）。
    /// ID 对齐：客户端传入 createdIdsSink 记录本次新建物品 ID 的顺序清单（提交后随封包发给宿主）；
    /// 宿主镜像传入 forcedIds，按客户端记录的顺序强制生成相同 ID —— 双端物品身份一致，
    /// 后续任何引用这些物品的联网操作都不会再被宿主以 "Could not find item" 拒绝。
    /// </summary>
    internal static DirectLoadResult CommitAfterReload(
        InventoryController controller,
        Magazine magazine,
        Weapon weapon,
        string stage,
        AmmoSelectionSnapshot selection,
        List<string>? createdIdsSink = null,
        IReadOnlyList<string>? forcedIds = null)
    {
        IDatabaseIdGenerator? idGenerator = null;
        if (forcedIds != null && forcedIds.Count > 0)
        {
            idGenerator = new ForcedIdGenerator(forcedIds, controller);
        }
        else if (createdIdsSink != null)
        {
            idGenerator = new RecordingIdGenerator(controller, createdIdsSink);
        }

        // 1) 先卸空弹匣：退出全部旧弹药（移回口袋/弹挂/背包；两端各自执行同一操作，指令序确定）
        var unloadError = UnloadMagazine(controller, magazine);
        if (unloadError != null)
        {
            SoftcoreReloadPatch.Dbg($"{stage}: magazine unload failed: {unloadError}");
            return new DirectLoadResult(0, 0, 0, $"magazine unload failed: {unloadError}");
        }

        // 2) 再按选择顺序填满（弹匣现在为空，容量即整匣）
        var piles = FindReachablePiles(controller, magazine, weapon);
        var ordered = OrderPiles(piles, selection);

        // 记录“最后使用弹药”候选：排序中第一种可实装的堆（填装从它开始消耗）
        var firstLive = ordered.FirstOrDefault(
            a => a != null && a.StackObjectsCount > 0 && a.CurrentAddress != null);

        var result = FillMagazine(controller, magazine, ordered, int.MaxValue, idGenerator);
        SoftcoreReloadPatch.LogDirectLoad(stage, magazine, result);
        if (result.Failed)
        {
            return result;
        }

        if (result.Loaded > 0 && firstLive != null)
        {
            AmmoSelectionState.MarkLastUsed(firstLive);
        }

        ChamberRound(weapon, controller, magazine, idGenerator);
        return result;
    }
}
