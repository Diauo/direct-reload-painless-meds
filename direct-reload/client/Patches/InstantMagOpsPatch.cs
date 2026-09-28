using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 弹匣快速装弹（Fika 同步版）：拦截 EFT.Player.PlayerInventoryController.LoadMagazine，
/// 跳过原版「逐发读秒」的异步操作，用与 Fika 自带瞬时装填相同的路线瞬间填满：
/// simulate 构造「装载操作」→ 经控制器执行入口提交（TryRunNetworkTransaction）。
/// 单机 = 本地标准执行；Fika 客户端 = 自动转发宿主验证（操作与物品 ID 双方同步推进）。
/// 前置检查顺序与原生一致：StopProcesses → 进程锁等待（method_30）。
/// 升级适配：LoadMagazine 为公开 override，按签名（AmmoItemClass, MagazineItemClass, int, bool）定位。
/// Target: SPT 4.0.13 / EFT 0.16.9.40087
/// </summary>
internal class InstantMagLoadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(EFT.Player.PlayerInventoryController)
                   .GetMethod(
                       "LoadMagazine",
                       BindingFlags.Public | BindingFlags.Instance,
                       null,
                       new[] { typeof(AmmoItemClass), typeof(MagazineItemClass), typeof(int), typeof(bool) },
                       null)
               ?? throw new InvalidOperationException(
                   "[DirectReload] PlayerInventoryController.LoadMagazine not found — game version mismatch?");
    }

    [PatchPrefix]
    private static bool Prefix(
        EFT.Player.PlayerInventoryController __instance,
        AmmoItemClass sourceAmmo,
        MagazineItemClass magazine,
        int loadCount,
        bool ignoreRestrictions,
        ref Task<IResult> __result)
    {
        if (!Plugin.InstantMagLoad.Value || loadCount <= 0)
        {
            return true;
        }

        __result = LoadInstantly(__instance, sourceAmmo, magazine, loadCount, ignoreRestrictions);
        return false;
    }

    private static async Task<IResult> LoadInstantly(
        EFT.Player.PlayerInventoryController controller,
        AmmoItemClass sourceAmmo,
        MagazineItemClass magazine,
        int loadCount,
        bool ignoreRestrictions)
    {
        try
        {
            controller.StopProcesses();

            // 与原生一致的进程锁等待（"Next process is locked."）。
            var gate = await controller.method_30();
            if (gate.Failed)
            {
                return gate;
            }

            var space = Math.Max(0, magazine.MaxCount - magazine.Count);
            var target = Math.Min(loadCount, Math.Min(space, sourceAmmo.StackObjectsCount));
            if (target <= 0)
            {
                return SuccessfulResult.New;
            }

            // 与 Fika 自带「瞬时装填」同一实现路线：simulate 构造操作 → 执行入口提交。
            var gstruct = ignoreRestrictions
                ? magazine.ApplyWithoutRestrictions(controller, sourceAmmo, target, simulate: true)
                : magazine.Apply(controller, sourceAmmo, target, simulate: true);
            if (gstruct.Failed)
            {
                return GClass1617.ToResult(gstruct);
            }

            var before = magazine.Count;
            var result = await controller.TryRunNetworkTransaction(gstruct, null);
            if (result.Failed)
            {
                return result;
            }

            if (magazine.Count <= before)
            {
                return new FailedResult(
                    $"RZ instant load executed but magazine count stayed at {magazine.Count}", 0);
            }

            magazine.RaiseRefreshEvent();
            sourceAmmo.RaiseRefreshEvent();

            if (Singleton<GUISounds>.Instantiated)
            {
                Singleton<GUISounds>.Instance.PlayUILoadSound();
            }

            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogInfo(
                    $"[DirectReload] instant mag load done: mag={magazine.Count}/{magazine.MaxCount}, " +
                    $"sourceLeft={sourceAmmo.StackObjectsCount}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] InstantMagLoad failed: {ex}");
            return new FailedResult($"RZ instant load exception: {ex.Message}", 0);
        }
    }
}

/// <summary>
/// 弹匣快速卸弹（原生瞬间例程版）：拦截 EFT.Player.PlayerInventoryController.UnloadMagazine，
/// 改调游戏自带的 InventoryController.UnloadAmmoInstantly —— 即基类默认的「瞬间卸弹」实现：
/// 用 QuickFindAppropriatePlace(UnloadAmmo) 整堆移动弹药、无逐发读秒。
/// 该例程内部走 TryRunNetworkTransaction（与 Fika 自带瞬时装填/卸弹同一条同步通道），
/// 无需额外处理 Fika 兼容。
/// 升级适配：UnloadMagazine 为公开 override，按签名（MagazineItemClass, bool）定位。
/// </summary>
internal class InstantMagUnloadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return typeof(EFT.Player.PlayerInventoryController)
                   .GetMethod(
                       "UnloadMagazine",
                       BindingFlags.Public | BindingFlags.Instance,
                       null,
                       new[] { typeof(MagazineItemClass), typeof(bool) },
                       null)
               ?? throw new InvalidOperationException(
                   "[DirectReload] PlayerInventoryController.UnloadMagazine not found — game version mismatch?");
    }

    [PatchPrefix]
    private static bool Prefix(
        EFT.Player.PlayerInventoryController __instance,
        MagazineItemClass magazine,
        bool equipmentBlocked,
        ref Task<IResult> __result)
    {
        if (!Plugin.InstantMagUnload.Value)
        {
            return true;
        }

        __result = UnloadInstantly(__instance, magazine, equipmentBlocked);
        return false;
    }

    private static async Task<IResult> UnloadInstantly(
        EFT.Player.PlayerInventoryController controller,
        MagazineItemClass magazine,
        bool equipmentBlocked)
    {
        try
        {
            controller.StopProcesses();

            var gate = await controller.method_30();
            if (gate.Failed)
            {
                return gate;
            }

            var result = await controller.UnloadAmmoInstantly(magazine, equipmentBlocked);

            if (Plugin.DebugLog.Value)
            {
                Plugin.Log.LogInfo(
                    $"[DirectReload] instant mag unload done: mag={magazine.Count}/{magazine.MaxCount}, " +
                    $"failed={result.Failed}");
            }

            return result;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[DirectReload] InstantMagUnload failed: {ex}");
            return new FailedResult($"RZ instant unload exception: {ex.Message}", 0);
        }
    }
}
