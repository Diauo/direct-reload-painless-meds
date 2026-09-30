using System;
using System.Reflection;
using EFT.UI;
using EFT.UI.Settings;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// FOV 解锁（SPT 4.0）：允许在游戏设置里选择高于原版上限（75）的视野值。
/// 两处补丁：
///   1) FovClampPatch —— 替换 FOV 设置项的数值钳制（原版 Mathf.Clamp(x, 50, 75)，位于 Class1841.method_0）为可配置区间；
///   2) FovSliderPatch —— 设置界面打开后，把 FOV 滑条重新绑定到可配置区间（原版 50..75）。
/// 目标经 IL 级核验（Class1841 = GClass1085 构造器使用的 int lambda 类，method_0 = FOV 钳制）。
/// 机制思路与 Fontaine's FOV Fix 相同（独立实现，仅参考其公开说明的挂点思路）。
/// </summary>
internal class FovClampPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var clampClass = AccessTools.TypeByName("Class1841")
                         ?? throw new InvalidOperationException(
                             "[DirectReload] Type 'Class1841' not found — game version mismatch?");

        return AccessTools.Method(clampClass, "method_0", new[] { typeof(int) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] Class1841.method_0(int) not found — game version mismatch?");
    }

    [PatchPostfix]
    private static void Postfix(int x, ref int __result)
    {
        if (!Plugin.EnableFovUnlock.Value)
        {
            return;
        }

        var min = Plugin.FovMin.Value;
        var max = Plugin.FovMax.Value;
        if (min > max)
        {
            min = 50;
            max = 75;
        }

        __result = Mathf.Clamp(x, min, max);
    }
}

/// <summary>设置界面的 FOV 滑条范围重绑定（原版 50..75）。</summary>
internal class FovSliderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        var sessionType = AccessTools.TypeByName("ISession")
                          ?? AccessTools.TypeByName("EFT.ISession")
                          ?? typeof(object);

        return AccessTools.Method(
                   typeof(GameSettingsTab),
                   "Show",
                   new[] { typeof(GClass1085), sessionType, typeof(bool) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] GameSettingsTab.Show(GClass1085, ISession, bool) not found — game version mismatch?");
    }

    [PatchPostfix]
    private static void Postfix(GameSettingsTab __instance, GClass1085 gameSettings, NumberSlider ____fov)
    {
        if (!Plugin.EnableFovUnlock.Value || gameSettings == null || ____fov == null)
        {
            return;
        }

        try
        {
            var min = (float)Plugin.FovMin.Value;
            var max = (float)Plugin.FovMax.Value;
            if (min > max)
            {
                min = 50f;
                max = 75f;
            }

#pragma warning disable CS0618 // 原版绑定 API（标记过时但行为等价，FOV Fix 同款用法）
            SettingsTab.BindNumberSliderToSetting(____fov, gameSettings.FieldOfView, min, max);
#pragma warning restore CS0618
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] FOV slider re-bind failed: {ex.Message}");
        }
    }
}
