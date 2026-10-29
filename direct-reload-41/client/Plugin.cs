using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using RZDirectReload.Client.Fika;
using RZDirectReload.Client.Inputs;
using RZDirectReload.Client.Patches;
using RZDirectReload.Client.Visuals;
using UnityEngine;

namespace RZDirectReload.Client;

/// <summary>
/// Direct Reload &amp; Painless Meds — client component (SPT 4.1 edition).
/// 软核装弹：按 R 后从可触及位置直接装填枪上弹匣；不使用备用弹匣。
///   覆盖标准弹匣武器 + 可拆弹匣且支持直装的武器（EXWIRS）；
///   纯弹仓武器（莫辛 / 固定弹仓 SKS 等）保持原版逐发装填（拟真平衡点）。
/// 弹药选择轮盘（1.3.0）：按住装填键弹出轮盘选定“优先弹药”，直装按选择取弹（不够自动降级）；
///   默认「自动」= 穿深最高的弹药优先；Fika 会话下选择随换弹同步至宿主镜像（零编译依赖）。
/// 批量手术：一次使用手术包治疗所有残疾肢体（每个消耗 1 点耐久）。
/// 弹匣瞬时操作：背包内弹匣装填/卸出子弹瞬间完成。
/// 配置界面为双语（中文 | English 双显）。
///
/// SPT 4.1 版说明：4.1 客户端部分类型为 PUA 单字符名，全部经 Refl 反射工具按字符串访问。
/// 每个补丁独立启用并 fail-soft：找不到目标的补丁只记日志跳过，不影响其余功能。
/// Target: SPT 4.1.6 / EFT 0.16.9.5.40743
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.rz99.directreload.client";
    public const string PluginName = "Direct Reload & Painless Meds";
    public const string PluginVersion = "1.4.0";

    internal static Plugin Instance = null!;

    internal static ManualLogSource Log = null!;

    // ── Reload ────────────────────────────────────────────
    /// <summary>Enable the "direct reload" feature.</summary>
    public static ConfigEntry<bool> EnableDirectLoad = null!;

    /// <summary>Show a native-style warning when a detected direct reload still fails.</summary>
    public static ConfigEntry<bool> EnableReloadFailureNotification = null!;

    // ── Ammo wheel ────────────────────────────────────────
    /// <summary>Enable the ammo selection wheel (hold reload key).</summary>
    public static ConfigEntry<bool> EnableAmmoWheel = null!;

    /// <summary>Press duration (ms) above which the wheel opens.</summary>
    public static ConfigEntry<int> WheelHoldMs = null!;

    /// <summary>Fallback reload key code (used when the game key-bind state cannot be read).</summary>
    public static ConfigEntry<KeyCode> WheelFallbackKey = null!;

    // ── Med animation sync ────────────────────────────────
    /// <summary>Match med/surgery animation speed to the server medUseTime multiplier.</summary>
    public static ConfigEntry<bool> EnableMedAnimSync = null!;

    /// <summary>Animation speed scale (= 1 / server medUseTime multiplier).</summary>
    public static ConfigEntry<float> MedAnimSpeedScale = null!;

    // ── Batch surgery ─────────────────────────────────────
    /// <summary>Enable "batch surgery": one use treats ALL blacked-out limbs.</summary>
    public static ConfigEntry<bool> EnableBatchSurgery = null!;

    /// <summary>手术在治好任何肢体之前被取消/打断时，退还游戏已扣的 1 点套件耐久。</summary>
    public static ConfigEntry<bool> RefundSurgeryOnCancel = null!;

    // ── Instant magazine ops ──────────────────────────────
    /// <summary>Instant magazine load (skip the per-round timer).</summary>
    public static ConfigEntry<bool> InstantMagLoad = null!;

    /// <summary>Instant magazine unload (native instant-unload routine).</summary>
    public static ConfigEntry<bool> InstantMagUnload = null!;

    // ── FOV unlock ────────────────────────────────────────
    /// <summary>Allow selecting a base FOV beyond the vanilla 75 cap.</summary>
    public static ConfigEntry<bool> EnableFovUnlock = null!;

    /// <summary>Selectable base FOV minimum.</summary>
    public static ConfigEntry<int> FovMin = null!;

    /// <summary>Selectable base FOV maximum.</summary>
    public static ConfigEntry<int> FovMax = null!;

    // ── Debug ─────────────────────────────────────────────
    public static ConfigEntry<bool> DebugLog = null!;

    // ── Wheels runtime ────────────────────────────────────
    private AmmoWheelOverlay? _wheelOverlay;
    private GameWorld? _lastWorld;
    private bool _updateErrorLogged;

    private void Awake()
    {
        Instance = this;
        Log = Logger;

        EnableDirectLoad = Config.Bind(
            "1. 装弹简化 | Reload",
            "启用直接装填 (Enable Direct Reload)",
            true,
            "启用后：按 R 从可触及位置（口袋/弹挂）消耗兼容散装弹药，使用枪上弹匣完成一次原版换弹动画；枪上弹匣不依赖备用弹匣。\n" +
            "必须能在口袋/弹挂找到一格暂存该弹匣；没有空格时会拒绝装填，不会瞬间填匣。\n" +
            "覆盖：标准弹匣武器 + 可拆弹匣且支持直装的武器（SKS-A 等）；纯弹仓武器（莫辛 / 固定弹仓 SKS）保持原版逐发装填，不受影响。\n" +
            "\n" +
            "Press R with compatible loose ammo within reach (pockets / rig) to fill the magazine on your gun in a single reload animation — no spare magazine required.\n" +
            "A free pocket/rig slot is needed to stow the current magazine; the reload is refused (with a warning) when no space is available — nothing is cheated in.\n" +
            "Covers: standard magazine weapons + detachable-mag weapons with direct-feed support (e.g. SKS-A).\n" +
            "Pure internal-magazine weapons (Mosin, fixed-mag SKS) keep vanilla one-by-one loading and are not affected.");

        EnableReloadFailureNotification = Config.Bind(
            "1. 装弹简化 | Reload",
            "装填失败时显示通知 (Show Reload Failure Notification)",
            true,
            "仅当口袋/弹挂中已经检测到兼容散装弹药，但装填事务仍失败时，使用塔科夫原生右下角警告提示（中英双语）。没有可用弹药时不会显示此提示。\n" +
            "\n" +
            "Shows the native bottom-right warning (bilingual) only when compatible loose ammo was detected but the reload transaction still failed. No warning when no usable ammo is available.");

        EnableAmmoWheel = Config.Bind(
            "7. 弹药轮盘 | Ammo Wheel",
            "启用弹药轮盘 (Enable Ammo Wheel)",
            true,
            "按住装填键（默认 R）弹出弹药选择轮盘：选定后，之后每次换弹会先退出弹匣内旧弹药，再按选择优先重装（用尽自动降级到其余弹药，按穿深从高到低）。\n" +
            "默认「自动」= 穿深最高的弹药优先；轮盘只列出弹匣与枪膛都兼容的弹药。轻按装填键 = 照常直装；轮盘只做选择、不触发换弹。\n" +
            "关闭 = 回到「按下即装」，不弹轮盘。\n" +
            "\n" +
            "Hold the reload key (default R) to open the ammo wheel; each subsequent reload first dumps the magazine's old rounds, then refills with the selected ammo first (auto-falls back to the rest, highest penetration first, when it runs out).\n" +
            "Default AUTO = highest-penetration ammo first; the wheel only lists ammo compatible with both magazine and chamber. A quick tap reloads as usual; the wheel only selects, it never reloads by itself.");

        WheelHoldMs = Config.Bind(
            "7. 弹药轮盘 | Ammo Wheel",
            "长按判定毫秒 (Hold Threshold ms)",
            180,
            new ConfigDescription(
                "按下时长超过该毫秒数视为「长按」（开轮盘），否则视为「轻按」（装弹）。\n" +
                "\n" +
                "Press-and-hold longer than this opens the wheel; a shorter tap reloads.",
                new AcceptableValueRange<int>(100, 500)));

        WheelFallbackKey = Config.Bind(
            "7. 弹药轮盘 | Ammo Wheel",
            "备用装填键 (Fallback Reload Key)",
            KeyCode.R,
            "仅当无法从游戏键位系统读取装填键实时状态时使用（优先跟随游戏内改键）。\n" +
            "\n" +
            "Used only as a fallback when the game key-bind state cannot be read (in-game rebinds take priority).");

        EnableMedAnimSync = Config.Bind(
            "2. 医疗动画 | Med Animation",
            "启用动画加速同步 (Sync Med Animation Speed)",
            true,
            "让医疗/手术动画的播放速度与服务端 medUseTime 加速同步，避免出现「效果已完成、动画还在播放」。\n" +
            "\n" +
            "Matches med/surgery animation playback speed to the server medUseTime multiplier — no more \"effect done, animation still playing\".");

        MedAnimSpeedScale = Config.Bind(
            "2. 医疗动画 | Med Animation",
            "动画速度倍率 (Animation Speed Scale)",
            2.0f,
            new ConfigDescription(
                "动画速度倍率 = 1 / 服务端 config.json 的 medUseTime multiplier。\n" +
                "例如服务端 0.5（时间减半），这里填 2.0（动画两倍速）。\n" +
                "\n" +
                "Animation speed scale = 1 / server config.json medUseTime multiplier.\n" +
                "E.g. server 0.5 (half time) -> 2.0 here (2x animation speed).",
                new AcceptableValueRange<float>(0.5f, 5f)));

        EnableBatchSurgery = Config.Bind(
            "3. 批量手术 | Batch Surgery",
            "启用批量手术 (Enable Batch Surgery)",
            true,
            "启用后：使用手术包（CMS / Surv12 / Sanitar）时，一次使用依次治疗所有残疾肢体，每个肢体消耗 1 点耐久。\n" +
            "\n" +
            "With a surgical kit (CMS / Surv12 / Sanitar), one use treats ALL blacked-out limbs (1 durability per limb) instead of one at a time.");

        RefundSurgeryOnCancel = Config.Bind(
            "3. 批量手术 | Batch Surgery",
            "取消也退耐久 (Refund Durability On Cancel)",
            false,
            "默认关闭 = 保持原版行为。开启后：手术在「一个肢体都没治好」的情况下被移动或取消打断时，把游戏已经扣掉的 1 点套件耐久退还。\n" +
            "注意：耐久校验（治好才扣）始终生效，与本项无关。\n" +
            "\n" +
            "Off (vanilla). When on, a surgery interrupted before healing any limb refunds the 1 kit durability the game already consumed. " +
            "The restore-then-verify rule (charge only when the limb is actually healed) is always active regardless of this switch.");

        InstantMagLoad = Config.Bind(
            "5. 弹匣快速操作 | Instant Magazine Ops",
            "弹匣快速装弹 (Instant Magazine Load)",
            true,
            "开启后：在背包内给弹匣装填子弹瞬间完成（直接装填，无读秒）。与直装同一套原语实现。\n" +
            "\n" +
            "Filling a magazine from loose ammo in your inventory completes instantly (no per-round timer) — same primitives as the direct reload.");

        InstantMagUnload = Config.Bind(
            "5. 弹匣快速操作 | Instant Magazine Ops",
            "弹匣快速卸弹 (Instant Magazine Unload)",
            true,
            "开启后：从弹匣中卸出子弹瞬间完成（整堆移动），适合从敌人弹匣里快速取弹。\n" +
            "\n" +
            "Emptying a magazine completes instantly (whole-stack native routine) — great for stripping ammo off dead enemies.");

        EnableFovUnlock = Config.Bind(
            "6. 视野 | FOV",
            "解锁 FOV 上限 (Unlock FOV Range)",
            true,
            "启用后：游戏设置里的视野（FOV）可在下方自定义区间内选择，突破原版上限 75。\n" +
            "调整 FOV 后需重启游戏/重进战局生效（与游戏自身设置项相同）。\n" +
            "\n" +
            "Lets the in-game FOV setting go beyond the vanilla 75 cap, within the custom range below.\n" +
            "Applied on settings change; takes effect when entering a raid (same as vanilla).");

        FovMin = Config.Bind(
            "6. 视野 | FOV",
            "FOV 最小值 (Min FOV)",
            50,
            new ConfigDescription(
                "设置里可选择的最小 FOV（原版 50）。\n" +
                "\n" +
                "Minimum selectable base FOV (vanilla: 50).",
                new AcceptableValueRange<int>(1, 200)));

        FovMax = Config.Bind(
            "6. 视野 | FOV",
            "FOV 最大值 (Max FOV)",
            110,
            new ConfigDescription(
                "设置里可选择的最大 FOV（原版上限 75，推荐 100~110；越高视野越广、目标越小）。\n" +
                "\n" +
                "Maximum selectable base FOV (vanilla cap: 75; 100-110 recommended).",
                new AcceptableValueRange<int>(1, 200)));

        DebugLog = Config.Bind(
            "4. 调试 | Debug",
            "输出调试日志 (Debug Logging)",
            false,
            "排查问题时开启（输出装弹 / 手术 / 动画 / 轮盘的详细日志）。\n" +
            "\n" +
            "Enable verbose logging for troubleshooting (reload / surgery / animation / wheel details).");

        TryEnable("SoftcoreReload", () => new SoftcoreReloadPatch().Enable());
        TryEnable("MedAnimSpeed", () => new MedAnimSpeedPatch().Enable());
        TryEnable("MedAnimatorSpeed", () => new MedAnimatorSpeedPatch().Enable());
        TryEnable("BatchSurgery", () => new BatchSurgeryPatch().Enable());
        TryEnable("SurgicalTeardown", () =>
        {
            new SurgicalOperationFinishPatch().Enable();
            new SurgicalOperationResetPatch().Enable();
            new SurgicalOperationRemovePatch().Enable();
        });
        TryEnable("InstantMagLoad", () => new InstantMagLoadPatch().Enable());
        TryEnable("InstantMagUnload", () => new InstantMagUnloadPatch().Enable());
        TryEnable("FovClamp", () => new FovClampPatch().Enable());
        TryEnable("FovSlider", () => new FovSliderPatch().Enable());
        TryEnable("FikaHostReloadMirror", () => new FikaHostReloadMirrorPatch().Enable());
        TryEnable("StateProbe", () => new StateProbePatch().Enable());
        TryEnable("AmmoWheelCommandBlock", () => new AmmoWheelCommandBlockPatch().Enable());
        TryEnable("AmmoWheelAxesBlock", () => new AmmoWheelAxesBlockPatch().Enable());
        TryEnable("AmmoWheelCaptureTree", () => new AmmoWheelCaptureTreePatch().Enable());
        TryEnable("AmmoWheelTranslatorCapture", () => new AmmoWheelTranslatorCapturePatch().Enable());

        Log.LogInfo(
            $"[DirectReload] Client plugin {PluginVersion} loaded — SPT 4.1 edition " +
            $"(reloadFailureNotice: {EnableReloadFailureNotification.Value}, ammoWheel: {EnableAmmoWheel.Value}, " +
            $"wheelHoldMs: {WheelHoldMs.Value}, animScale: {MedAnimSpeedScale.Value}, " +
            $"batchSurgery: {EnableBatchSurgery.Value}, instantMagLoad: {InstantMagLoad.Value}, " +
            $"instantMagUnload: {InstantMagUnload.Value})");
    }

    /// <summary>每帧：战局边界重置 + 轮盘状态机 + Fika 桥 + 轮盘覆盖层。</summary>
    private void Update()
    {
        try
        {
            var world = Singleton<GameWorld>.Instance;
            if (!ReferenceEquals(world, _lastWorld))
            {
                _lastWorld = world;
                AmmoSelectionState.ResetAll();
                AmmoWheelController.ForceClose();
            }

            AmmoWheelController.PollKeys();
            FikaAmmoSelectionBridge.Tick();

            // 无头端（-batchmode / -nographics）不创建轮盘覆盖层
            if (!Application.isBatchMode)
            {
                _wheelOverlay ??= new AmmoWheelOverlay();
                _wheelOverlay.Update();
            }
        }
        catch (Exception ex)
        {
            if (!_updateErrorLogged)
            {
                _updateErrorLogged = true;
                Log.LogError($"[DirectReload] Update loop error (further errors suppressed): {ex}");
            }
        }
    }

    private void OnDestroy()
    {
        try
        {
            AmmoWheelController.ForceClose();
            _wheelOverlay?.Dispose();
            _wheelOverlay = null;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"[DirectReload] teardown failed: {ex.Message}");
        }
    }

    /// <summary>每个补丁独立启用：任何目标定位失败只记日志，不影响其它功能。</summary>
    private void TryEnable(string name, Action enable)
    {
        try
        {
            enable();
            Log.LogInfo($"[DirectReload] Patch enabled: {name}");
        }
        catch (Exception ex)
        {
            Log.LogError($"[DirectReload] Patch FAILED to enable: {name} — {ex.Message}");
        }
    }

    /// <summary>宿主端镜像提交调度（票据驱动：与客户端本次换弹的选择/提交信封一一对应）。</summary>
    internal void ScheduleMirrorCommit(
        InventoryController controller,
        Weapon weapon,
        Magazine magazine,
        AmmoSelectionSnapshot selection,
        string ticket)
    {
        StartCoroutine(MirrorCommitRoutine(controller, weapon, magazine, selection, ticket));
    }

    private System.Collections.IEnumerator MirrorCommitRoutine(
        InventoryController controller,
        Weapon weapon,
        Magazine magazine,
        AmmoSelectionSnapshot selection,
        string ticket)
    {
        try
        {
            yield return new UnityEngine.WaitForSeconds(1.0f);

            // 等本票据对应的提交信封（= 客户端确实完成了直装提交）。
            // 等到才提交（强制 ID 对齐）；等不到就跳过 —— 绝不无对齐幽灵提交。
            var deadline = UnityEngine.Time.realtimeSinceStartup + 6f;
            while (!FikaAmmoSelectionBridge.HasIdsForMirror(ticket)
                   && UnityEngine.Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (!FikaAmmoSelectionBridge.TakeIdsForMirror(ticket, out var payload)
                || string.IsNullOrEmpty(payload)
                || !CommitIds.TryDecode(payload, out var committedIds))
            {
                if (!_idSkipLogged)
                {
                    _idSkipLogged = true;
                    Log.LogInfo(
                        "[DirectReload] host mirror skipped: no client commit envelope " +
                        "(vanilla reload, lost message, or participant version mismatch) — host left untouched");
                }
                else
                {
                    SoftcoreReloadPatch.Dbg("host mirror skipped (no envelope)");
                }

                yield break;
            }

            try
            {
                var result = AmmoCommit.CommitAfterReload(
                    controller, magazine, weapon, "host-mirror", selection, null, committedIds);
                Log.LogInfo(
                    $"[DirectReload] host mirror commit done: failed={result.Failed}, " +
                    $"loaded={result.Loaded}, alignedIds={committedIds.Count}");
            }
            catch (Exception ex)
            {
                Log.LogError($"[DirectReload] Host mirror commit failed: {ex}");
            }
        }
        finally
        {
            FikaAmmoSelectionBridge.FinishMirror(ticket);
        }
    }

    private bool _idSkipLogged;
}
