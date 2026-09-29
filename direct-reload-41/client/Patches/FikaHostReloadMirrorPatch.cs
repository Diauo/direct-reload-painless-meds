using System;
using System.Reflection;
using System.Collections;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// Fika 宿主端镜像提交 — SPT 4.1 版。
/// 客户端侧软核换弹的「补填 + 补膛」是本地提交；Fika 会话下宿主必须对它的玩家副本执行同一组提交
/// （Fika 对装填类变更的原生并行提交模式），否则宿主与客户端物品状态漂移。
///
/// 做法：在宿主上拦截「被观察玩家」的换弹入口（Fika 的 ObservedFirearmController.ReloadMag，
/// 经类型名反射启用，Fika 缺失/版本不符时静默跳过），识别软核特征 —— 弹匣本体已装在枪上
/// （原版换弹永远是换备用弹匣），延迟 2 秒后对宿主的玩家副本运行同一套提交。
/// 仅当运行时存在 Fika 且当前进程是宿主（FikaBackendUtils.IsServer）时触发；单机 / 客户端不触发。
/// </summary>
internal class FikaHostReloadMirrorPatch : ModulePatch
{
    private const string ObservedControllerTypeName =
        "Fika.Core.Main.ObservedClasses.HandsControllers.ObservedFirearmController";

    protected override MethodBase GetTargetMethod()
    {
        var type = AccessTools.TypeByName(ObservedControllerTypeName)
                   ?? throw new InvalidOperationException(
                       "[DirectReload] Fika ObservedFirearmController not found — Fika absent or version mismatch?");

        return AccessTools.Method(
                   type,
                   "ReloadMag",
                   new[] { typeof(Magazine), typeof(ItemAddress), typeof(Callback) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] ObservedFirearmController.ReloadMag not found — Fika version mismatch?");
    }

    [PatchPostfix]
    private static void Postfix(Magazine magazine, ItemAddress itemAddress)
    {
        try
        {
            if (!Plugin.EnableDirectLoad.Value || magazine == null)
            {
                return;
            }

            // 软核特征：弹匣本体就在枪上（原版换弹换的是口袋/弹挂里的备用弹匣）。
            if (magazine.Parent?.Container is not Slot slot || slot.ParentItem is not Weapon weapon)
            {
                return;
            }

            // 只在 Fika 宿主（权威侧）执行镜像。
            if (!IsFikaServer())
            {
                return;
            }

            var controller = magazine.Parent.GetOwner() as InventoryController;
            if (controller == null)
            {
                SoftcoreReloadPatch.Dbg("host mirror skipped: controller not resolvable from magazine owner");
                return;
            }

            // 宿主自己的玩家不做镜像（其换弹走本地输入路径、已在回调里自行提交）。
            var mainPlayer = Singleton<GameWorld>.Instance?.MainPlayer;
            if (mainPlayer != null && ReferenceEquals(controller, mainPlayer.InventoryController))
            {
                return;
            }

            Plugin.Log.LogInfo($"[DirectReload] host mirror armed: mag={magazine.Id}, weapon={weapon.Id}");
            Plugin.Instance.ScheduleMirrorCommit(controller, weapon, magazine);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] Host reload mirror postfix failed: {ex}");
        }
    }

    /// <summary>Fika 宿主判定（反射 FikaBackendUtils.IsServer；无 Fika 类型 → false）。</summary>
    internal static bool IsFikaServer()
    {
        try
        {
            var type = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
            var prop = type?.GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static);
            return prop != null && prop.GetValue(null) is true;
        }
        catch
        {
            return false;
        }
    }
}
