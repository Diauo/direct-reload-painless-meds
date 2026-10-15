using System;
using System.Reflection;
using System.Reflection.Emit;

namespace RZDirectReload.Client.Fika;

/// <summary>选择同步包的自定义接口（让泛型 handler 能读取字段，无需编译期 Fika 依赖）。</summary>
public interface IRzAmmoSelectionPacket
{
    string OwnerId { get; }

    string TemplateId { get; }

    bool IsAuto { get; }
}

/// <summary>
/// 运行时生成选择同步包类型（Reflection.Emit）：
///   · 类型全名固定 = "RZDirectReload.Client.Fika.AmmoSelectionPacket"（双端一致 → Fika 短哈希一致）；
///   · 实现 Fika 的 INetSerializable（Serialize/Deserialize 用 NetDataWriter/NetDataReader）；
///   · 字段：OwnerId(string) → TemplateId(string) → IsAuto(bool)，顺序固定。
/// 零编译期 Fika 依赖；找不到 Fika 类型时返回 null（功能静默降级）。
/// </summary>
internal static class FikaPacketEmitter
{
    internal const string PacketTypeName = "RZDirectReload.Client.Fika.AmmoSelectionPacket";

    private static bool _attempted;
    private static Type? _packetType;
    private static Exception? _failure;

    internal static string? Failure => _failure?.Message;

    internal static Type? PacketType
    {
        get
        {
            Ensure();
            return _packetType;
        }
    }

    private static void Ensure()
    {
        if (_attempted)
        {
            return;
        }

        _attempted = true;

        try
        {
            var ifikaSerializable = FindType("Fika.Core.Networking.LiteNetLib.Utils.INetSerializable");
            var writerType = FindType("Fika.Core.Networking.LiteNetLib.Utils.NetDataWriter");
            var readerType = FindType("Fika.Core.Networking.LiteNetLib.Utils.NetDataReader");
            if (ifikaSerializable == null || writerType == null || readerType == null)
            {
                _failure = new InvalidOperationException("Fika packet types not found (INetSerializable/NetDataWriter/NetDataReader)");
                return;
            }

            var putString = writerType.GetMethod("Put", new[] { typeof(string), typeof(int) });
            var putBool = writerType.GetMethod("Put", new[] { typeof(bool) });
            var getString = readerType.GetMethod("GetString", Type.EmptyTypes);
            var getBool = readerType.GetMethod("GetBool", Type.EmptyTypes);
            if (putString == null || putBool == null || getString == null || getBool == null)
            {
                _failure = new InvalidOperationException("Fika serializer methods not found (Put/GetString/GetBool)");
                return;
            }

            var assembly = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName("RZDirectReload.Fika.Dynamic"), AssemblyBuilderAccess.Run);
            var module = assembly.DefineDynamicModule("RZDirectReload.Fika.Dynamic");

            var tb = module.DefineType(
                PacketTypeName,
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class,
                typeof(object),
                new[] { ifikaSerializable, typeof(IRzAmmoSelectionPacket) });

            var fieldOwner = tb.DefineField("_ownerId", typeof(string), FieldAttributes.Private);
            var fieldTemplate = tb.DefineField("_templateId", typeof(string), FieldAttributes.Private);
            var fieldAuto = tb.DefineField("_isAuto", typeof(bool), FieldAttributes.Private);

            // 无参构造：字符串字段初始化为 ""（满足 new() 约束 + 空值安全）
            var ctor = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, Type.EmptyTypes);
            var ctorIl = ctor.GetILGenerator();
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Call, typeof(object).GetConstructor(Type.EmptyTypes)!);
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldstr, string.Empty);
            ctorIl.Emit(OpCodes.Stfld, fieldOwner);
            ctorIl.Emit(OpCodes.Ldarg_0);
            ctorIl.Emit(OpCodes.Ldstr, string.Empty);
            ctorIl.Emit(OpCodes.Stfld, fieldTemplate);
            ctorIl.Emit(OpCodes.Ret);

            DefineProperty(tb, "OwnerId", typeof(string), fieldOwner);
            DefineProperty(tb, "TemplateId", typeof(string), fieldTemplate);
            DefineProperty(tb, "IsAuto", typeof(bool), fieldAuto);

            // Serialize(NetDataWriter) — 顺序：OwnerId → TemplateId → IsAuto
            var serialize = tb.DefineMethod(
                "Serialize",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
                MethodAttributes.Final | MethodAttributes.HideBySig,
                typeof(void),
                new[] { writerType });
            var serIl = serialize.GetILGenerator();
            serIl.Emit(OpCodes.Ldarg_1);
            serIl.Emit(OpCodes.Ldarg_0);
            serIl.Emit(OpCodes.Ldfld, fieldOwner);
            serIl.Emit(OpCodes.Ldc_I4_0);
            serIl.Emit(OpCodes.Callvirt, putString);
            serIl.Emit(OpCodes.Ldarg_1);
            serIl.Emit(OpCodes.Ldarg_0);
            serIl.Emit(OpCodes.Ldfld, fieldTemplate);
            serIl.Emit(OpCodes.Ldc_I4_0);
            serIl.Emit(OpCodes.Callvirt, putString);
            serIl.Emit(OpCodes.Ldarg_1);
            serIl.Emit(OpCodes.Ldarg_0);
            serIl.Emit(OpCodes.Ldfld, fieldAuto);
            serIl.Emit(OpCodes.Callvirt, putBool);
            serIl.Emit(OpCodes.Ret);

            // Deserialize(NetDataReader)
            var deserialize = tb.DefineMethod(
                "Deserialize",
                MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
                MethodAttributes.Final | MethodAttributes.HideBySig,
                typeof(void),
                new[] { readerType });
            var desIl = deserialize.GetILGenerator();
            desIl.Emit(OpCodes.Ldarg_0);
            desIl.Emit(OpCodes.Ldarg_1);
            desIl.Emit(OpCodes.Callvirt, getString);
            desIl.Emit(OpCodes.Stfld, fieldOwner);
            desIl.Emit(OpCodes.Ldarg_0);
            desIl.Emit(OpCodes.Ldarg_1);
            desIl.Emit(OpCodes.Callvirt, getString);
            desIl.Emit(OpCodes.Stfld, fieldTemplate);
            desIl.Emit(OpCodes.Ldarg_0);
            desIl.Emit(OpCodes.Ldarg_1);
            desIl.Emit(OpCodes.Callvirt, getBool);
            desIl.Emit(OpCodes.Stfld, fieldAuto);
            desIl.Emit(OpCodes.Ret);

            _packetType = tb.CreateType();
        }
        catch (Exception ex)
        {
            _failure = ex;
        }
    }

    private static void DefineProperty(TypeBuilder tb, string name, Type type, FieldBuilder field)
    {
        var property = tb.DefineProperty(name, PropertyAttributes.None, type, null);

        var getter = tb.DefineMethod(
            "get_" + name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
            MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            type,
            Type.EmptyTypes);
        var getIl = getter.GetILGenerator();
        getIl.Emit(OpCodes.Ldarg_0);
        getIl.Emit(OpCodes.Ldfld, field);
        getIl.Emit(OpCodes.Ret);

        var setter = tb.DefineMethod(
            "set_" + name,
            MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
            MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.SpecialName,
            typeof(void),
            new[] { type });
        var setIl = setter.GetILGenerator();
        setIl.Emit(OpCodes.Ldarg_0);
        setIl.Emit(OpCodes.Ldarg_1);
        setIl.Emit(OpCodes.Stfld, field);
        setIl.Emit(OpCodes.Ret);

        property.SetGetMethod(getter);
        property.SetSetMethod(setter);
    }

    /// <summary>跨程序集类型解析（BCL-only：先 Type.GetType，再扫描已加载程序集）。</summary>
    internal static Type? FindType(string fullName)
    {
        try
        {
            var direct = Type.GetType(fullName);
            if (direct != null)
            {
                return direct;
            }
        }
        catch
        {
            // ignore
        }

        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }
}
