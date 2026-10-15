using System;
using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InputSystem;
using EFT.Settings;
using UnityEngine;

namespace RZDirectReload.Client.Inputs;

/// <summary>
/// 装填键“按住 / 松开”检测（v8：与 RZSkill 轮盘呼出统一——纯 Unity 逐帧轮询）。
///
/// 背景：游戏命令的触发时机取决于绑定触发类型（本机为 Release 型——松手才发命令），
/// 因此手势不依赖命令时机，改为逐帧轮询按键状态、自行做边缘检测（RZSkills 同款机制）。
/// 读数只取自 Unity（游戏窗口内）：窗口未激活时一律视为“未按下”——
/// 切屏到其它程序（如 QQ）按键不会触发任何装填/轮盘行为。
/// 本类只负责两件事：
///   · 解析候选键（设置绑定键 ∪ 配置回退键；含触发类型诊断输出）；
///   · 读取候选键的当前状态（窗口未激活 → 未按下）。
/// 由 AmmoWheelController 每帧调用。
/// </summary>
internal static class AmmoWheelKeyState
{
    /// <summary>InputTree 实例（由 AmmoWheelCaptureTreePatch 捕获；保留备查）。</summary>
    internal static InputTree? Tree;

    private static bool _settingsPathWarned;

    private static readonly List<KeyCode> _cachedCandidates = new();
    private static string _cachedDetail = "settings(n/a)";
    private static float _cachedAt = -100f;

    /// <summary>候选键列表（每 2 秒重读一次设置；跟随游戏内改键）。</summary>
    internal static List<KeyCode> GetCandidateKeys(out string detail)
    {
        float now = Time.unscaledTime;
        if (_cachedCandidates.Count > 0 && now - _cachedAt < 2f)
        {
            detail = _cachedDetail;
            return _cachedCandidates;
        }

        _cachedCandidates.Clear();
        string pressTypeInfo = "?";

        try
        {
            var codes = GetBoundKeyCodes(out pressTypeInfo);
            if (codes != null && codes.Count > 0)
            {
                foreach (var keyCode in codes)
                {
                    if (keyCode != KeyCode.None && !_cachedCandidates.Contains(keyCode))
                    {
                        _cachedCandidates.Add(keyCode);
                    }
                }

                _cachedDetail = $"settings(codes=[{string.Join(",", codes)}],pressType={pressTypeInfo})";
            }
            else
            {
                _cachedDetail = $"settings(no-codes,pressType={pressTypeInfo})";
            }
        }
        catch (Exception ex)
        {
            _cachedDetail = "settings(error)";
            if (!_settingsPathWarned)
            {
                _settingsPathWarned = true;
                Plugin.Log.LogWarning($"[DirectReload] reload key-bind read failed, falling back: {ex.Message}");
            }
        }

        var fallback = Plugin.WheelFallbackKey.Value;
        if (fallback != KeyCode.None && !_cachedCandidates.Contains(fallback))
        {
            _cachedCandidates.Add(fallback);
        }

        _cachedAt = now;
        detail = _cachedDetail;
        return _cachedCandidates;
    }

    /// <summary>读取候选键状态（仅 Unity；窗口未激活时恒为未按下）。</summary>
    internal static bool ReadDown(List<KeyCode> candidates, out string detail)
    {
        bool focused = true;
        try
        {
            focused = Application.isFocused;
        }
        catch
        {
            // ignore
        }

        if (!focused)
        {
            detail = "blur(window inactive)";
            return false;
        }

        var samples = new List<string>();
        bool held = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            var keyCode = candidates[i];

            bool down;
            try
            {
                down = Input.GetKey(keyCode);
            }
            catch
            {
                down = false;
            }

            samples.Add($"{keyCode}(u={(down ? "D" : "-")})");
            if (down)
            {
                held = true;
            }
        }

        detail = $"keys=[{string.Join(" ", samples)}]";
        return held;
    }

    /// <summary>读取 ReloadWeapon 的设置绑定键列表（收集所有匹配组；含触发类型）。</summary>
    private static List<KeyCode>? GetBoundKeyCodes(out string pressTypeInfo)
    {
        pressTypeInfo = "?";
        var settings = Singleton<SettingsManager>.Instance?.Control?.Settings;
        var groups = settings?.UserKeyBindings?.Value;
        if (groups == null)
        {
            return null;
        }

        var codes = new List<KeyCode>();
        for (int i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            if (group == null || group.keyName != EGameKey.ReloadWeapon || group.variants == null)
            {
                continue;
            }

            if (pressTypeInfo == "?")
            {
                pressTypeInfo = group.pressType.ToString();
            }

            foreach (var variant in group.variants)
            {
                if (variant?.keyCode == null)
                {
                    continue;
                }

                foreach (var keyCode in variant.keyCode)
                {
                    if (keyCode != KeyCode.None)
                    {
                        codes.Add(keyCode);
                    }
                }
            }
        }

        return codes;
    }
}
