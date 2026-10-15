using EFT;
using EFT.InventoryLogic;
using RZDirectReload.Client.Patches;

namespace RZDirectReload.Client;

/// <summary>弹匣取弹优先级模式。</summary>
internal enum AmmoSelectionMode
{
    /// <summary>自动：穿深降序（默认）。</summary>
    Auto,

    /// <summary>指定弹药模板优先，不够自动降级。</summary>
    Explicit,
}

/// <summary>换弹开始时捕获的选择快照（用于双端一致的取弹排序 + Fika 同步）。</summary>
internal readonly struct AmmoSelectionSnapshot
{
    internal static readonly AmmoSelectionSnapshot Auto = new(AmmoSelectionMode.Auto, null);

    internal AmmoSelectionSnapshot(AmmoSelectionMode mode, string? templateId)
    {
        Mode = mode;
        TemplateId = templateId;
    }

    internal AmmoSelectionMode Mode { get; }

    internal string? TemplateId { get; }
}

/// <summary>轮盘条目/最后使用弹药的展示信息（名字、数值、图标）。</summary>
internal sealed class AmmoDisplayInfo
{
    internal string TemplateId = string.Empty;

    internal string Name = string.Empty;

    internal int Damage;

    internal int Pen;

    /// <summary>当前可达数量（“最后使用”缓存快照时为 0）。</summary>
    internal int Count;

    /// <summary>游戏图标对象（Sprite 可能异步补全，可为 null）。</summary>
    internal ItemIcon? Icon;

    internal AmmoDisplayInfo Snapshot()
    {
        return new AmmoDisplayInfo
        {
            TemplateId = TemplateId,
            Name = Name,
            Damage = Damage,
            Pen = Pen,
            Count = 0,
            Icon = Icon,
        };
    }
}

/// <summary>
/// 弹药选择状态（战局级，内存）。战局边界（GameWorld 引用变化）时由 Plugin 重置。
/// </summary>
internal sealed class AmmoSelectionState
{
    internal static AmmoSelectionState Current = new();

    internal AmmoSelectionMode Mode = AmmoSelectionMode.Auto;

    internal string? TemplateId;

    /// <summary>本局最后一次“实际装进枪”的弹药（或轮盘显式选择的弹药）；用于空弹灰色展示。</summary>
    internal AmmoDisplayInfo? LastUsed;

    internal AmmoSelectionSnapshot Snapshot()
    {
        return Mode == AmmoSelectionMode.Explicit && !string.IsNullOrEmpty(TemplateId)
            ? new AmmoSelectionSnapshot(AmmoSelectionMode.Explicit, TemplateId)
            : AmmoSelectionSnapshot.Auto;
    }

    internal void SelectAuto()
    {
        Mode = AmmoSelectionMode.Auto;
        TemplateId = null;
    }

    internal void SelectExplicit(AmmoDisplayInfo entry)
    {
        Mode = AmmoSelectionMode.Explicit;
        TemplateId = entry.TemplateId;
        LastUsed = entry.Snapshot();
    }

    /// <summary>装填提交成功后记录“最后使用”。</summary>
    internal static void MarkLastUsed(Ammo pile)
    {
        if (pile == null || Current == null)
        {
            return;
        }

        try
        {
            var info = new AmmoDisplayInfo
            {
                TemplateId = pile.TemplateId.ToString(),
                Name = SafeShortName(pile),
                Damage = pile.Damage,
                Pen = pile.PenetrationPower,
                Count = 0,
                Icon = AmmoCatalog.TryLoadIcon(pile),
            };
            Current.LastUsed = info;
        }
        catch (Exception ex)
        {
            SoftcoreReloadPatch.Dbg($"mark last-used failed: {ex.Message}");
        }
    }

    private static string SafeShortName(Ammo ammo)
    {
        try
        {
            var name = ammo.LocalizedShortName();
            if (!string.IsNullOrEmpty(name))
            {
                return name;
            }
        }
        catch
        {
            // fall through
        }

        try
        {
            return ammo.Name;
        }
        catch
        {
            return ammo.TemplateId.ToString();
        }
    }

    /// <summary>战局边界重置（选择归零 + Fika 存储清空）。</summary>
    internal static void ResetAll()
    {
        Current = new AmmoSelectionState();
        Fika.FikaAmmoSelectionBridge.ClearStore();
    }
}
