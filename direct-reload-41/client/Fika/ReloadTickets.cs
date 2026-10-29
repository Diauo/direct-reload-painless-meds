using System;
using System.Collections.Generic;

namespace RZDirectReload.Client.Fika;

/// <summary>
/// 每换弹票据（采纳社区维护包设计）：线上 owner 字段携带 "dr2:&lt;guid&gt;:&lt;owner&gt;" 形式的票据键，
/// 把一次换弹的"选择"与"提交 ID 清单"绑定在一起；宿主侧按票据隔离待处理项，
/// 防止两次重叠换弹的选择/ID 互相混淆或覆盖（旧实现每人只有一个待定槽）。
///
/// Credit: per-reload ticket scheme adopted from txvicious's reviewed companion packet
/// (GitHub issue #1, MIT) — https://github.com/Diauo/direct-reload-painless-meds/issues/1
/// Re-implemented inside this mod; see ACKNOWLEDGEMENTS.md.
/// </summary>
internal sealed class ReloadTickets
{
    private const string Prefix = "dr2:";

    private readonly Dictionary<string, string> _local = new();
    private readonly Dictionary<string, string> _latest = new();
    private readonly Dictionary<string, string> _pending = new();
    private readonly HashSet<string> _scheduled = new();
    private readonly object _gate = new();

    /// <summary>客户端：为本次换弹开票（选择消息发出前调用）。</summary>
    internal string BeginLocal(string owner)
    {
        var key = Prefix + Guid.NewGuid().ToString("N") + ":" + owner;
        lock (_gate)
        {
            _local[owner] = key;
        }

        return key;
    }

    /// <summary>客户端：取出本 owner 最近开的票（提交 ID 发送时调用，一次性）。</summary>
    internal bool TakeLocal(string owner, out string key)
    {
        lock (_gate)
        {
            if (!_local.TryGetValue(owner, out key!))
            {
                return false;
            }

            _local.Remove(owner);
            return true;
        }
    }

    /// <summary>解析票据键 → 玩家 ID。非票据格式返回 false。</summary>
    internal static bool TryOwner(string key, out string owner)
    {
        owner = string.Empty;
        if (key.Length <= Prefix.Length + 33
            || !key.StartsWith(Prefix, StringComparison.Ordinal)
            || key[Prefix.Length + 32] != ':'
            || !Guid.TryParseExact(key.Substring(Prefix.Length, 32), "N", out _))
        {
            return false;
        }

        owner = key.Substring(Prefix.Length + 33);
        return owner.Length > 0;
    }

    /// <summary>
    /// 宿主：登记一条选择消息。新的未武装选择会顶替孤儿（如 ReloadMag 失败的旧选择）；
    /// 已武装（已入 pending）的换弹保持隔离。返回被顶替的孤儿票据（供清理 store）。
    /// </summary>
    internal string? ReceiveSelection(string key)
    {
        if (!TryOwner(key, out var owner))
        {
            return null;
        }

        lock (_gate)
        {
            _latest.TryGetValue(owner, out var orphan);
            _latest[owner] = key;
            return orphan != key ? orphan : null;
        }
    }

    /// <summary>宿主：把该玩家最近的待定选择转成镜像任务票据。</summary>
    internal bool PrepareSchedule(string owner, out string key)
    {
        lock (_gate)
        {
            if (!_latest.TryGetValue(owner, out key!))
            {
                return false;
            }

            _latest.Remove(owner);
            _pending[owner] = key;
            _scheduled.Add(key);
            return true;
        }
    }

    /// <summary>宿主：取走待镜像票据（启动镜像协程时调用，一次性）。</summary>
    internal bool TakePending(string owner, out string key)
    {
        lock (_gate)
        {
            if (!_pending.TryGetValue(owner, out key!))
            {
                return false;
            }

            _pending.Remove(owner);
            return true;
        }
    }

    /// <summary>宿主：镜像结束清理。</summary>
    internal void Finish(string key)
    {
        lock (_gate)
        {
            _scheduled.Remove(key);
            if (TryOwner(key, out var owner) && _pending.TryGetValue(owner, out var pending) && pending == key)
            {
                _pending.Remove(owner);
            }
        }
    }

    /// <summary>该票据是否仍是已排定的镜像任务（用于孤儿选择清理）。</summary>
    internal bool IsScheduled(string key)
    {
        lock (_gate)
        {
            return _scheduled.Contains(key);
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _local.Clear();
            _latest.Clear();
            _pending.Clear();
            _scheduled.Clear();
        }
    }
}

/// <summary>
/// 提交 ID 信封（采纳社区维护包设计）：整堆移动/合并可能产生零个新 ID，
/// 而线上 Ids 字段为空 = "选择消息"——所以每次成功提交都必须携带一个非空的版本化信封，
/// 空清单也发（"提交发生了、但没有新建 ID"）。
/// </summary>
internal static class CommitIds
{
    private const string Prefix = "dr2-ids:";

    internal static string Encode(IReadOnlyList<string> ids) => Prefix + string.Join(",", ids);

    internal static bool TryDecode(string payload, out IReadOnlyList<string> ids)
    {
        ids = Array.Empty<string>();
        if (!payload.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var csv = payload.Substring(Prefix.Length);
        if (csv.Length == 0)
        {
            return true;
        }

        var values = csv.Split(',');
        foreach (var value in values)
        {
            if (value.Length != 24)
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
        }

        ids = values;
        return true;
    }
}
