using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Comfort.Common;
using EFT.InventoryLogic;
using HarmonyLib;
using RZDirectReload.Client.Patches;

namespace RZDirectReload.Client.Fika;

/// <summary>
/// Fika 选择同步桥（零编译依赖，全反射）：
///   · 客户端：换弹开始瞬间把「本轮选择」发给宿主（在 ReloadMag 之前，同一通道有序）；
///   · 宿主：惰性注册接收器，按玩家存储选择；镜像提交时取用（缺失 → Auto + 警告）。
/// 单机 / 无 Fika 时全部为空操作。
/// </summary>
internal static class FikaAmmoSelectionBridge
{
    private sealed class StoredSelection
    {
        internal string? TemplateId;
        internal bool IsAuto;
    }

    private static readonly Dictionary<string, StoredSelection> _store = new();

    private static bool _resolutionDone;
    private static Type? _managerInterfaceType;
    private static PropertyInfo? _singletonInstanceProp;
    private static MethodInfo? _sendDataOpenGeneric;
    private static MethodInfo? _registerOpenGeneric;

    private static object? _registeredManager;
    private static bool _registerWarned;
    private static bool _sendWarned;
    private static bool _missingStoredWarned;

    /// <summary>Fika 管理器接口类型（无 Fika → null）。</summary>
    internal static bool FikaPresent
    {
        get
        {
            EnsureResolution();
            return _managerInterfaceType != null;
        }
    }

    /// <summary>管理器解析节流间隔（秒）：无会话时绝不做每帧扫描（性能红线）。</summary>
    private const float ManagerRetrySeconds = 2f;

    private static float _nextManagerAttempt;

    /// <summary>每帧（Plugin.Update）：宿主侧惰性注册接收器（管理器解析按 ManagerRetrySeconds 节流）。</summary>
    internal static void Tick()
    {
        EnsureResolution();
        if (_managerInterfaceType == null)
        {
            return;
        }

        try
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now < _nextManagerAttempt)
            {
                return;
            }

            _nextManagerAttempt = now + ManagerRetrySeconds;

            var manager = GetManager();
            if (manager == null)
            {
                _registeredManager = null;
                return;
            }

            if (ReferenceEquals(manager, _registeredManager))
            {
                return;
            }

            _registeredManager = manager;

            if (_registerWarned || !IsServer())
            {
                return; // 仅宿主注册
            }

            var packetType = FikaPacketEmitter.PacketType;
            if (packetType == null || _registerOpenGeneric == null)
            {
                WarnOnce(ref _registerWarned, $"ammo-selection register skipped: {FikaPacketEmitter.Failure ?? "packet type unavailable"}");
                return;
            }

            var register = _registerOpenGeneric.MakeGenericMethod(packetType);
            var handler = typeof(FikaAmmoSelectionBridge)
                .GetMethod(nameof(OnPacketReceivedGeneric), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(packetType);
            var actionType = typeof(Action<>).MakeGenericType(packetType);
            var del = Delegate.CreateDelegate(actionType, handler);
            register.Invoke(manager, new object[] { del });
            Plugin.Log.LogInfo("[DirectReload] ammo-selection packet registered on Fika host");
        }
        catch (Exception ex)
        {
            _registerWarned = true;
            Plugin.Log.LogWarning($"[DirectReload] ammo-selection register failed (sync disabled this session): {ex.Message}");
        }
    }

    /// <summary>客户端侧：发送本轮选择（在 hands.ReloadMag 之前调用）。</summary>
    internal static void SendSelection(string ownerId, AmmoSelectionSnapshot selection)
    {
        try
        {
            EnsureResolution();
            if (_managerInterfaceType == null || _sendDataOpenGeneric == null)
            {
                return; // 无 Fika：单机本地路径，无需发送
            }

            if (string.IsNullOrEmpty(ownerId))
            {
                return;
            }

            var isClient = ReadBackendFlag("IsClient");
            if (isClient != true)
            {
                return; // 仅客户端发送（宿主自身玩家为本地提交）
            }

            var packetType = FikaPacketEmitter.PacketType;
            if (packetType == null)
            {
                WarnOnce(ref _sendWarned, $"ammo-selection send skipped: {FikaPacketEmitter.Failure ?? "packet type unavailable"}");
                return;
            }

            var manager = GetManager();
            if (manager == null)
            {
                return;
            }

            var packet = Activator.CreateInstance(packetType);
            if (packet == null)
            {
                return;
            }

            SetProperty(packetType, packet, "OwnerId", ownerId);
            SetProperty(packetType, packet, "TemplateId", selection.TemplateId ?? string.Empty);
            SetProperty(packetType, packet, "IsAuto", selection.Mode == AmmoSelectionMode.Auto);

            var deliveryType = FikaPacketEmitter.FindType("Fika.Core.Networking.LiteNetLib.DeliveryMethod");
            var reliableOrdered = deliveryType != null ? Enum.Parse(deliveryType, "ReliableOrdered") : null;
            if (reliableOrdered == null)
            {
                WarnOnce(ref _sendWarned, "ammo-selection send skipped: DeliveryMethod not found");
                return;
            }

            var send = _sendDataOpenGeneric.MakeGenericMethod(packetType);
            send.Invoke(manager, new object[] { packet!, reliableOrdered, false });
        }
        catch (Exception ex)
        {
            WarnOnce(ref _sendWarned, $"ammo-selection send failed (host mirror may fall back to auto): {ex.Message}");
        }
    }

    /// <summary>宿主镜像侧：取该玩家的选择；返回 false = 未命中（调用方回退 Auto + 警告）。</summary>
    internal static bool TryGetForMirror(string ownerId, out AmmoSelectionSnapshot snapshot)
    {
        snapshot = AmmoSelectionSnapshot.Auto;
        try
        {
            if (!string.IsNullOrEmpty(ownerId) && _store.TryGetValue(ownerId, out var stored))
            {
                snapshot = stored.IsAuto
                    ? AmmoSelectionSnapshot.Auto
                    : new AmmoSelectionSnapshot(AmmoSelectionMode.Explicit, stored.TemplateId);
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    /// <summary>战局边界清空（AmmoSelectionState.ResetAll 调用）。</summary>
    internal static void ClearStore()
    {
        try
        {
            _store.Clear();
            _missingStoredWarned = false;
        }
        catch
        {
            // ignore
        }
    }

    internal static void WarnMissingForMirror(string ownerId)
    {
        if (_missingStoredWarned)
        {
            return;
        }

        _missingStoredWarned = true;
        Plugin.Log.LogWarning(
            $"[DirectReload] ammo selection missing for mirror (owner={ownerId}) — falling back to auto (possible divergence)");
    }

    // ── 内部 ────────────────────────────────────────────────

    private static void OnPacketReceivedGeneric<T>(T packet)
    {
        try
        {
            if (packet is IRzAmmoSelectionPacket selection)
            {
                var ownerId = selection.OwnerId ?? string.Empty;
                if (ownerId.Length == 0)
                {
                    return;
                }

                _store[ownerId] = new StoredSelection
                {
                    TemplateId = selection.TemplateId,
                    IsAuto = selection.IsAuto,
                };
                SoftcoreReloadPatch.Dbg($"selection packet received: owner={ownerId}, template={selection.TemplateId}, auto={selection.IsAuto}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DirectReload] ammo-selection packet handling failed: {ex.Message}");
        }
    }

    private static void EnsureResolution()
    {
        if (_resolutionDone)
        {
            return;
        }

        _resolutionDone = true;

        try
        {
            _managerInterfaceType = FikaPacketEmitter.FindType("Fika.Core.Networking.IFikaNetworkManager");
            if (_managerInterfaceType == null)
            {
                return;
            }

            try
            {
                var singletonOpen = typeof(Singleton<>);
                var closed = singletonOpen.MakeGenericType(_managerInterfaceType);
                _singletonInstanceProp = closed.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            }
            catch
            {
                _singletonInstanceProp = null;
            }

            _sendDataOpenGeneric = _managerInterfaceType
                .GetMethods()
                .FirstOrDefault(m => m.Name == "SendData" && m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
            _registerOpenGeneric = _managerInterfaceType
                .GetMethods()
                .FirstOrDefault(m => m.Name == "RegisterPacket" && m.IsGenericMethodDefinition && m.GetParameters().Length == 1);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[DirectReload] Fika bridge resolution failed: {ex.Message}");
            _managerInterfaceType = null;
        }
    }

    private static Type? _fikaClientType;
    private static Type? _fikaServerType;

    private static object? GetManager()
    {
        // 主路径：Comfort Singleton<IFikaNetworkManager>.Instance。
        // 性能红线：单例机制可用时绝不落回 Unity 全场景扫描——
        // 无会话状态下每帧 FindObjectOfType 会全场景遍历（菜单/加载巨卡、单核满载）。
        if (_singletonInstanceProp != null)
        {
            try
            {
                var instance = _singletonInstanceProp.GetValue(null);
                if (instance is UnityEngine.Object unityObject)
                {
                    return unityObject != null ? instance : null;
                }

                return instance; // 可能为 null = 管理器缺席（正常菜单态）
            }
            catch
            {
                return null;
            }
        }

        // 备用路径：仅当单例机制本身无法解析时（具体类型一次解析后缓存；调用侧已按秒节流）。
        try
        {
            _fikaClientType ??= FikaPacketEmitter.FindType("Fika.Core.Networking.FikaClient");
            _fikaServerType ??= FikaPacketEmitter.FindType("Fika.Core.Networking.FikaServer");
            if (_fikaClientType != null)
            {
                var found = UnityEngine.Object.FindObjectOfType(_fikaClientType);
                if (found != null)
                {
                    return found;
                }
            }

            if (_fikaServerType != null)
            {
                var found = UnityEngine.Object.FindObjectOfType(_fikaServerType);
                if (found != null)
                {
                    return found;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static PropertyInfo? _isServerProp;
    private static PropertyInfo? _isClientProp;
    private static bool _backendPropsResolved;

    private static bool? ReadBackendFlag(string propertyName)
    {
        try
        {
            if (!_backendPropsResolved)
            {
                _backendPropsResolved = true;
                var type = AccessTools.TypeByName("Fika.Core.Main.Utils.FikaBackendUtils");
                _isServerProp = type?.GetProperty("IsServer", BindingFlags.Public | BindingFlags.Static);
                _isClientProp = type?.GetProperty("IsClient", BindingFlags.Public | BindingFlags.Static);
            }

            var property = propertyName == "IsServer" ? _isServerProp : _isClientProp;
            var value = property?.GetValue(null);
            return value is bool flag ? flag : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsServer() => ReadBackendFlag("IsServer") == true;

    private static void SetProperty(Type packetType, object packet, string name, object value)
    {
        var property = packetType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        property?.SetValue(packet, value);
    }

    private static void WarnOnce(ref bool warned, string message)
    {
        if (warned)
        {
            return;
        }

        warned = true;
        Plugin.Log.LogWarning($"[DirectReload] {message}");
    }
}
