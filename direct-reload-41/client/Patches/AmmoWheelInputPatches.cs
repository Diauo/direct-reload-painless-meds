using System.Reflection;
using EFT;
using EFT.InputSystem;
using HarmonyLib;
using SPT.Reflection.Patching;

namespace RZDirectReload.Client.Patches;

/// <summary>弹药轮盘展开时：拦掉全部游戏操作指令（防误射 / 误交互）。</summary>
internal class AmmoWheelCommandBlockPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateCommand));

    [PatchPrefix]
    private static bool Prefix(ref InputNode.ETranslateResult __result)
    {
        if (!Inputs.AmmoWheelGate.IsOpen)
        {
            return true;
        }

        __result = InputNode.ETranslateResult.BlockAll;
        return false;
    }
}

/// <summary>
/// 弹药轮盘展开时：只冻结视角 / 转向轴（防视角乱转），**保留移动轴**——人物可边开轮盘边移动。
/// 轴语义（源自 TranslateAxes）：[0,1]=移动  [2,3]=转向  [4,5]=视角  [6]=侧身。
/// </summary>
internal class AmmoWheelAxesBlockPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.TranslateAxes));

    [PatchPrefix]
    private static bool Prefix(ref float[] axes)
    {
        if (!Inputs.AmmoWheelGate.IsOpen)
        {
            return true;
        }

        if (axes != null)
        {
            for (int i = 2; i <= 5 && i < axes.Length; i++)
            {
                axes[i] = 0f;
            }
        }

        return true; // 不跳过原逻辑：移动（[0,1]）照常生效
    }
}

/// <summary>捕获火器输入翻译器（持枪 / 换枪即构造——开局第一按前即就位）。</summary>
internal class AmmoWheelTranslatorCapturePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Constructor(
            typeof(FirearmHandsInputTranslator),
            new[] { typeof(Player), typeof(IFirearmHandsController) });

    [PatchPostfix]
    private static void Postfix(FirearmHandsInputTranslator __instance)
    {
        Inputs.AmmoWheelController.NoteInstance(__instance);
    }
}

/// <summary>捕获 InputTree 实例（按键实时状态检测的备选路径）。</summary>
internal class AmmoWheelCaptureTreePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod() =>
        AccessTools.Method(typeof(InputTree), "Awake");

    [PatchPostfix]
    private static void Postfix(InputTree __instance)
    {
        Inputs.AmmoWheelKeyState.Tree = __instance;
    }
}
