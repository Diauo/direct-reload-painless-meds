using System;
using System.Collections.Generic;
using EFT;
using EFT.InventoryLogic;
using RZDirectReload.Client.Patches;
using UnityEngine;

namespace RZDirectReload.Client.Inputs;

/// <summary>轮盘展开状态标志（供输入冻结补丁查询）。</summary>
internal static class AmmoWheelGate
{
    internal static bool IsOpen;
}

/// <summary>轮盘条目（“自动”或某种弹药，含灰色不可选态）。</summary>
internal sealed class WheelSlot
{
    internal bool IsAuto;

    internal AmmoDisplayInfo? Ammo;

    internal bool Selected;

    /// <summary>灰色不可选（空弹时展示的“最后使用”）。</summary>
    internal bool Unavailable;
}

/// <summary>
/// 弹药选择轮盘控制器（轻按=装 / 长按=轮盘 / 轮盘只选不装）。
///
/// v8 设计（与 RZSkills 轮盘呼出统一）：
///   · 每帧用 Unity Input 轮询装填键（窗口未激活时不读数——切屏不会误触发），
///     自行做按下/松开边缘检测——不依赖游戏命令的触发时机（Press / Release 触发型均正确工作）；
///   · 按下 → 计入手势；松开 &lt; 阈值 → 轻按 = 直装；按住 ≥ 阈值 → 开轮盘；
///   · 上下文（翻译器/武器）优先活取当前玩家，开局第一按即可用；命令到达时另行更新缓存；
///   · 游戏发出的装填命令一律被前缀消费并由 NoteCommand 记入"本轮意图"；
///   · 轮盘内：悬停高亮、滚轮翻页、松开/点击=确认选择（不触发换弹）、中心松开=取消。
/// </summary>
internal static class AmmoWheelController
{
    private const int SlotsPerPage = 7;
    private const float SelectionDeadzonePx = 40f;
    private const float PostGestureSuppressSeconds = 0.5f;
    private const float DeferredCommandSeconds = 0.3f;

    private enum Phase
    {
        Idle,
        Hold,
        Open,
    }

    private static Phase _phase;
    private static float _pressTime = -1f;
    private static bool _keyWasDown;

    /// <summary>本轮手势对应的装填命令时间（前缀消费时记录）；手势被处理后清空。</summary>
    private static float? _pendingCommandAt;

    /// <summary>手势刚结束时短暂静默消费迟到命令（Release 触发型的松开命令等）。</summary>
    private static float _suppressCommandsUntil;

    /// <summary>命令上下文（供轻按直装 / 轮盘取用；前缀记录）。</summary>
    private static FirearmHandsInputTranslator? _lastInstance;
    private static Weapon? _lastWeapon;

    private static readonly List<WheelSlot> _slots = new();
    private static readonly List<WheelSlot> _allSlots = new();
    private static int _page;
    private static int _highlighted = -1;
    private static AmmoDisplayInfo? _currentSelectionInfo;

    internal static bool IsOpen => _phase == Phase.Open;

    internal static int HighlightedIndex => _highlighted;

    internal static int Page => _page;

    internal static int PageCount => Math.Max(1, (_allSlots.Count + SlotsPerPage - 1) / SlotsPerPage);

    internal static IReadOnlyList<WheelSlot> Slots => _slots;

    /// <summary>当前显式选择对应的条目（可能为空 = 自动或不在场）。</summary>
    internal static AmmoDisplayInfo? CurrentSelectionInfo => _currentSelectionInfo;

    /// <summary>装填命令被消费时记录（由 SoftcoreReloadPatch 前缀调用）。</summary>
    internal static void NoteCommand(FirearmHandsInputTranslator instance, Weapon weapon)
    {
        _lastInstance = instance;
        _lastWeapon = weapon;

        float now = Time.unscaledTime;
        if (now >= _suppressCommandsUntil)
        {
            _pendingCommandAt = now;
        }
        else
        {
            SoftcoreReloadPatch.Dbg("wheel: command consumed (post-gesture window)");
        }
    }

    /// <summary>每帧驱动（Plugin.Update）：物理键监视 + 手势状态机 + 轮盘 UI。</summary>
    internal static void PollKeys()
    {
        if (!Plugin.EnableDirectLoad.Value || !Plugin.EnableAmmoWheel.Value)
        {
            if (_phase != Phase.Idle)
            {
                ForceClose();
            }

            _keyWasDown = false;
            return;
        }

        float now = Time.unscaledTime;
        var candidates = AmmoWheelKeyState.GetCandidateKeys(out var keyDetail);
        bool down = AmmoWheelKeyState.ReadDown(candidates, out var readDetail);

        // UI 遮挡（背包/容器/菜单打开中）：装填键归 UI 使用（例如 R 旋转物品），手势不介入。
        if (IsUiBlocking())
        {
            if (_keyWasDown || _phase != Phase.Idle)
            {
                SoftcoreReloadPatch.Dbg("gesture: aborted (ui open — inventory/menu)");
            }

            _phase = Phase.Idle;
            _pressTime = -1f;
            _pendingCommandAt = null;
            _keyWasDown = false;
            return;
        }

        if (down && !_keyWasDown)
        {
            OnKeyDown(now, keyDetail, readDetail);
        }
        else if (!down && _keyWasDown)
        {
            OnKeyUp(now, readDetail);
        }

        _keyWasDown = down;

        if (_phase == Phase.Hold)
        {
            if (down && now - _pressTime >= Plugin.WheelHoldMs.Value / 1000f)
            {
                TryOpenWheel();
            }
        }
        else if (_phase == Phase.Open)
        {
            UpdateWheel(now);
        }

        // 迟到命令兜底（超快轻按等未被监视器捕获边缘的情况）
        if (_pendingCommandAt.HasValue
            && _phase == Phase.Idle
            && !down
            && now - _pendingCommandAt.Value > DeferredCommandSeconds
            && now >= _suppressCommandsUntil)
        {
            _pendingCommandAt = null;
            SoftcoreReloadPatch.Dbg("wheel: deferred command → tap");
            RunTapReload();
        }
    }

    /// <summary>强制收起（战局结束 / 清理时调用）。</summary>
    internal static void ForceClose()
    {
        if (_phase == Phase.Open)
        {
            CloseWheelInternal();
        }

        _phase = Phase.Idle;
        _pendingCommandAt = null;
        _keyWasDown = false;
    }

    // ── UI 遮挡 ─────────────────────────────────────────────

    /// <summary>
    /// 是否有 UI 遮挡（背包 / 容器 / 搜刮 / 菜单打开中）：此时装填键的语义归 UI
    /// （例如背包里 R = 旋转物品），本 mod 的手势不介入。
    /// 判定三合一（任一成立即视为遮挡）：玩家背包标志 ∪ 手部控制器 IsInventoryOpen()
    /// （游戏自身判定：手部处于背包姿态）∪ 光标未锁定。轮盘自身展开时除外。
    /// </summary>
    private static bool IsUiBlocking()
    {
        if (AmmoWheelGate.IsOpen)
        {
            return false;
        }

        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player != null)
            {
                if (player.IsInventoryOpened)
                {
                    return true;
                }

                var hands = player.HandsController as Player.FirearmController;
                if (hands != null && hands.IsInventoryOpen())
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignore — 走光标兜底
        }

        try
        {
            // 只用可见性：战斗/瞄准时光标不可见；背包/菜单等需要光标时可见。
            // （不用 lockState —— 战斗态锁态并非必然 Locked，避免误判把正常装填也拦掉）
            return Cursor.visible;
        }
        catch
        {
            return false;
        }
    }

    // ── 手势边缘 ────────────────────────────────────────────

    private static void OnKeyDown(float now, string keyDetail, string readDetail)
    {
        if (_phase == Phase.Open)
        {
            SoftcoreReloadPatch.Dbg("gesture: key down ignored (wheel open)");
            return;
        }

        _pressTime = now;
        _phase = Phase.Hold;
        _pendingCommandAt = null; // 本次手势接管意图
        SoftcoreReloadPatch.Dbg($"gesture: key down ({keyDetail}; {readDetail})");
    }

    private static void OnKeyUp(float now, string readDetail)
    {
        if (_phase == Phase.Hold)
        {
            float dt = now - _pressTime;
            _phase = Phase.Idle;
            _pendingCommandAt = null;

            if (dt < Plugin.WheelHoldMs.Value / 1000f)
            {
                _suppressCommandsUntil = now + PostGestureSuppressSeconds;
                SoftcoreReloadPatch.Dbg($"gesture: tap (dt={dt * 1000f:0}ms; {readDetail})");
                RunTapReload();
            }
            else
            {
                SoftcoreReloadPatch.Dbg($"gesture: release after threshold (dt={dt * 1000f:0}ms)");
            }

            return;
        }

        if (_phase == Phase.Open)
        {
            FinishWheelSelection(now);
        }
    }

    // ── 上下文解析 ──────────────────────────────────────────

    /// <summary>翻译器构造捕获（持枪 / 换枪即刷新；由 AmmoWheelTranslatorCapturePatch 调用）。</summary>
    internal static void NoteInstance(FirearmHandsInputTranslator instance)
    {
        _lastInstance = instance;
        SoftcoreReloadPatch.Dbg("translator captured (hands controller ready)");
    }

    /// <summary>
    /// 解析当前火器输入翻译器：使用最近捕获的实例（构造补丁在每次持枪 / 换枪时刷新——
    /// 开局第一按即可用），并以当前玩家的手部控制器做一致性校验：手持物已切换
    /// （空手 / 投掷物 / 换枪间隙）时直接拒绝，绝不使用旧实例（新翻译器构造后会重新捕获）。
    /// </summary>
    private static FirearmHandsInputTranslator? ResolveInstance()
    {
        var instance = _lastInstance;
        if (instance == null)
        {
            return null;
        }

        try
        {
            var player = GamePlayerOwner.MyPlayer;
            if (player != null)
            {
                var hands = instance._controller;
                var current = player.HandsController;
                if (hands == null || current == null || !ReferenceEquals(current, hands))
                {
                    return null;
                }
            }
        }
        catch (Exception ex)
        {
            SoftcoreReloadPatch.Dbg($"translator consistency check failed: {ex.Message}");
        }

        return instance;
    }

    // ── 轻按路径 ────────────────────────────────────────────

    private static void RunTapReload()
    {
        if (IsUiBlocking())
        {
            SoftcoreReloadPatch.Dbg("tap reload skipped: ui open");
            return;
        }

        var instance = ResolveInstance();
        if (instance == null)
        {
            SoftcoreReloadPatch.Dbg("tap reload skipped: no input translator available");
            return;
        }

        var hands = instance._controller;
        var weapon = hands?.Item as Weapon;
        if (hands == null || weapon == null)
        {
            SoftcoreReloadPatch.Dbg("tap reload skipped: no firearm in hands");
            return;
        }

        // 非直装武器（内置管仓/转轮等）：命令路径已按原版执行（前缀放行），
        // 手势轻按不再重复转发，避免双重换弹。
        if (!SoftcoreReloadPatch.IsDirectLoadCapable(weapon))
        {
            SoftcoreReloadPatch.Dbg("tap skipped: not direct-load capable (command path handles vanilla)");
            return;
        }

        bool result = true;
        bool vanilla = SoftcoreReloadPatch.TrySoftcoreReload(instance, weapon, ref result);
        if (vanilla)
        {
            SoftcoreReloadPatch.ForwardToVanilla(instance, weapon, false);
        }
    }

    // ── 轮盘 ────────────────────────────────────────────────

    private static void TryOpenWheel()
    {
        if (IsUiBlocking())
        {
            _phase = Phase.Idle;
            SoftcoreReloadPatch.Dbg("wheel open skipped: ui open");
            return;
        }

        var instance = ResolveInstance();
        if (instance == null)
        {
            _phase = Phase.Idle;
            SoftcoreReloadPatch.Dbg("wheel: no input translator available");
            return;
        }

        var hands = instance._controller;
        var weapon = hands?.Item as Weapon;
        if (hands == null || weapon == null)
        {
            _phase = Phase.Idle;
            SoftcoreReloadPatch.Dbg("wheel: no firearm in hands");
            return;
        }

        if (!hands.CanStartReload())
        {
            _phase = Phase.Idle;
            _pendingCommandAt = null;
            SoftcoreReloadPatch.NotifyReloadFailure(
                "Cannot start a reload right now",
                "当前状态无法开始换弹",
                "CanStartReload=false (wheel)");
            return;
        }

        _pendingCommandAt = null; // 手势已被轮盘接管
        OpenWheel(instance, weapon);
    }

    private static void OpenWheel(FirearmHandsInputTranslator instance, Weapon weapon)
    {
        _allSlots.Clear();
        _currentSelectionInfo = null;

        var selection = AmmoSelectionState.Current;
        bool autoSelected = selection.Mode == AmmoSelectionMode.Auto;
        _allSlots.Add(new WheelSlot { IsAuto = true, Ammo = null, Selected = autoSelected });

        List<AmmoDisplayInfo> entries;
        try
        {
            var controller = instance._inventoryController;
            var magazine = weapon.GetCurrentMagazine();
            entries = controller != null && magazine != null
                ? AmmoCatalog.BuildEntries(controller, magazine, weapon)
                : new List<AmmoDisplayInfo>();
        }
        catch (Exception ex)
        {
            SoftcoreReloadPatch.Dbg($"catalog build failed: {ex.Message}");
            entries = new List<AmmoDisplayInfo>();
        }

        foreach (var entry in entries)
        {
            bool selected = !autoSelected && selection.TemplateId == entry.TemplateId;
            _allSlots.Add(new WheelSlot { IsAuto = false, Ammo = entry, Selected = selected });
            if (selected)
            {
                _currentSelectionInfo = entry;
            }
        }

        // 空弹：展示灰色“最后使用”（不可选）
        if (entries.Count == 0 && selection.LastUsed != null)
        {
            _allSlots.Add(new WheelSlot
            {
                IsAuto = false,
                Ammo = selection.LastUsed.Snapshot(),
                Selected = false,
                Unavailable = true,
            });
        }

        _page = 0;
        _highlighted = -1;
        RebuildPage();
        _phase = Phase.Open;
        AmmoWheelGate.IsOpen = true;
        CursorGate.Enter();
        Plugin.Log.LogInfo($"[DirectReload] ammo wheel opened: {_allSlots.Count} slot(s), {PageCount} page(s)");
    }

    private static void UpdateWheel(float now)
    {
        UpdateHighlight();
        HandleScroll();

        if (Input.GetMouseButtonDown(0) && _highlighted >= 0)
        {
            CommitHighlighted();
            _suppressCommandsUntil = now + PostGestureSuppressSeconds;
            _pendingCommandAt = null;
            CloseWheelInternal();
            Plugin.Log.LogInfo("[DirectReload] wheel closed (click commit)");
        }
    }

    /// <summary>松开装填键：有高亮=确认选择；否则取消。随后关闭（不触发换弹）。</summary>
    private static void FinishWheelSelection(float now)
    {
        if (_highlighted >= 0)
        {
            CommitHighlighted();
        }

        _suppressCommandsUntil = now + PostGestureSuppressSeconds;
        _pendingCommandAt = null;
        CloseWheelInternal();
        Plugin.Log.LogInfo("[DirectReload] wheel closed (release)");
    }

    private static void CloseWheelInternal()
    {
        _phase = Phase.Idle;
        AmmoWheelGate.IsOpen = false;
        CursorGate.Exit();
        _slots.Clear();
        _allSlots.Clear();
        _highlighted = -1;
        _page = 0;
    }

    private static void RebuildPage()
    {
        _slots.Clear();
        int start = _page * SlotsPerPage;
        for (int i = start; i < _allSlots.Count && i < start + SlotsPerPage; i++)
        {
            _slots.Add(_allSlots[i]);
        }
    }

    private static void ChangePage(int delta)
    {
        int count = PageCount;
        if (count <= 1)
        {
            return;
        }

        int next = ((_page + delta) % count + count) % count;
        if (next == _page)
        {
            return;
        }

        _page = next;
        _highlighted = -1;
        RebuildPage();
    }

    private static void HandleScroll()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (scroll > 0.01f)
        {
            ChangePage(-1);
        }
        else if (scroll < -0.01f)
        {
            ChangePage(1);
        }
    }

    private static void UpdateHighlight()
    {
        int count = _slots.Count;
        if (count == 0)
        {
            _highlighted = -1;
            return;
        }

        var center = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
        var delta = (Vector2)Input.mousePosition - center;
        if (delta.magnitude < SelectionDeadzonePx)
        {
            _highlighted = -1;
            return;
        }

        float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
        if (angle < 0f)
        {
            angle += 360f;
        }

        float sector = 360f / count;
        int index = Mathf.FloorToInt(((90f - angle + 360f) % 360f) / sector);
        _highlighted = Mathf.Clamp(index, 0, count - 1);
    }

    private static void CommitHighlighted()
    {
        if (_highlighted < 0 || _highlighted >= _slots.Count)
        {
            return;
        }

        var slot = _slots[_highlighted];
        if (slot.Unavailable)
        {
            return;
        }

        if (slot.IsAuto)
        {
            AmmoSelectionState.Current.SelectAuto();
            Plugin.Log.LogInfo("[DirectReload] ammo selection -> AUTO");
            return;
        }

        if (slot.Ammo != null)
        {
            AmmoSelectionState.Current.SelectExplicit(slot.Ammo);
            Plugin.Log.LogInfo(
                $"[DirectReload] ammo selection -> {slot.Ammo.Name} ({slot.Ammo.TemplateId}), pen={slot.Ammo.Pen}, count={slot.Ammo.Count}");
        }
    }
}
