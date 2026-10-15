using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using RZDirectReload.Client.Patches;

namespace RZDirectReload.Client;

/// <summary>
/// 轮盘数据组装：从官方可达接口扫描 → 按模板分组 → 穿深降序（同穿深按模板 ID 定序）。
/// 来源=官方 GetReachableItemsOfTypeNonAlloc（与游戏换弹同一通道；含第三方扩展）。
/// </summary>
internal static class AmmoCatalog
{
    /// <summary>扫描并构建弹药条目（不含“自动”项）。weapon 提供枪膛兼容过滤（排除枪打不了的弹药）。</summary>
    internal static List<AmmoDisplayInfo> BuildEntries(InventoryController controller, Magazine magazine, Weapon? weapon)
    {
        var list = new List<AmmoDisplayInfo>();
        var byTemplate = new Dictionary<string, AmmoDisplayInfo>();

        List<Ammo> piles;
        try
        {
            piles = AmmoCommit.FindReachablePiles(controller, magazine, weapon);
        }
        catch (Exception ex)
        {
            SoftcoreReloadPatch.Dbg($"catalog scan failed: {ex.Message}");
            return list;
        }

        foreach (var ammo in piles)
        {
            if (ammo == null || ammo.StackObjectsCount <= 0 || ammo.CurrentAddress == null)
            {
                continue;
            }

            string templateId;
            try
            {
                templateId = ammo.TemplateId.ToString();
            }
            catch
            {
                continue;
            }

            if (!byTemplate.TryGetValue(templateId, out var info))
            {
                info = new AmmoDisplayInfo
                {
                    TemplateId = templateId,
                    Name = SafeName(ammo),
                    Damage = ammo.Damage,
                    Pen = ammo.PenetrationPower,
                    Count = 0,
                    Icon = TryLoadIcon(ammo),
                };
                byTemplate.Add(templateId, info);
                list.Add(info);
            }

            info.Count += ammo.StackObjectsCount;
        }

        list.Sort((x, y) =>
        {
            int pen = y.Pen.CompareTo(x.Pen);
            if (pen != 0)
            {
                return pen;
            }

            return string.CompareOrdinal(x.TemplateId, y.TemplateId);
        });

        return list;
    }

    /// <summary>游戏官方图标助手（自带缺失 prefab 的兜底图标）。失败返回 null（绘制占位框）。</summary>
    internal static ItemIcon? TryLoadIcon(Ammo ammo)
    {
        try
        {
            return ItemViewFactory.LoadItemIcon(ammo);
        }
        catch (Exception ex)
        {
            SoftcoreReloadPatch.Dbg($"icon load failed: {ex.Message}");
            return null;
        }
    }

    private static string SafeName(Ammo ammo)
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
}
