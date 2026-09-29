using System;
using System.Reflection;
using System.Threading.Tasks;
using Comfort.Common;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>
/// 弹匣快速装弹 — SPT 4.1 版（强类型）。
/// 拦截 EFT.Player.PlayerInventoryController.LoadMagazine（\uE011-时代的原版前置检查保持一致）：
/// StopProcesses → WaitForProcess 进程门 → simulate 构造装载操作 → 执行入口提交（TryRunNetworkTransaction，
/// 单机本地执行 / Fika 客户端自动转发宿主，与 Fika 自带瞬时装填同路线）。
/// </summary>
internal class InstantMagLoadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
                   typeof(EFT.Player.PlayerInventoryController),
                   "LoadMagazine",
                   new[] { typeof(Ammo), typeof(Magazine), typeof(int), typeof(bool) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] PlayerInventoryController.LoadMagazine not found — game version mismatch?");
    }

    [PatchPrefix]
    private static bool Prefix(
        EFT.Player.PlayerInventoryController __instance,
        Ammo sourceAmmo,
        Magazine magazine,
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
        Ammo sourceAmmo,
        Magazine magazine,
        int loadCount,
        bool ignoreRestrictions)
    {
        try
        {
            controller.StopProcesses();

            var space = Math.Max(0, magazine.MaxCount - magazine.Count);
            var target = Math.Min(loadCount, Math.Min(space, sourceAmmo.StackObjectsCount));
            if (target <= 0)
            {
                return SuccessfulResult.New;
            }

            var gate = await controller.WaitForProcess();
            if (gate.Failed)
            {
                return gate;
            }

            // 与原生 LoadMagazine 相同：simulate 构造操作。
            var sim = ignoreRestrictions
                ? magazine.ApplyWithoutRestrictions(controller, sourceAmmo, target, simulate: true)
                : magazine.Apply(controller, sourceAmmo, target, simulate: true);
            if (sim.Failed)
            {
                return new FailedResult(
                    sim.Error != null ? sim.Error.ToString() : "RZ instant load: apply failed", 0);
            }

            var before = magazine.Count;
            var result = await controller.TryRunNetworkTransaction(sim, null);
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
/// 弹匣快速卸弹 — SPT 4.1 版：拦截 UnloadMagazine，改调游戏自带的瞬间卸弹例程
/// InventoryController.UnloadAmmoInstantly（整堆移动，走 TryRunNetworkTransaction 同步通道）。
/// </summary>
internal class InstantMagUnloadPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
    {
        return AccessTools.Method(
                   typeof(EFT.Player.PlayerInventoryController),
                   "UnloadMagazine",
                   new[] { typeof(Magazine), typeof(bool) })
               ?? throw new InvalidOperationException(
                   "[DirectReload] PlayerInventoryController.UnloadMagazine not found — game version mismatch?");
    }

    [PatchPrefix]
    private static bool Prefix(
        EFT.Player.PlayerInventoryController __instance,
        Magazine magazine,
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
        Magazine magazine,
        bool equipmentBlocked)
    {
        try
        {
            controller.StopProcesses();

            var gate = await controller.WaitForProcess();
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
