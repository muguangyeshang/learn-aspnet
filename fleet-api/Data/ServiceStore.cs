using System.Collections.Concurrent;
using System.Text.Json;
using FleetApi.Models;

namespace FleetApi.Data;

/// <summary>
/// 服务表的内存实现，语义对齐 Spring 的 ServiceRepository：
/// 端口全局唯一、reporter 全量覆盖式上报。
/// 并发写用 _gate 串行化，保证「删旧 + 写新」是一个原子动作。
/// 复杂度：List/Replace 为 O(n)，n = 表行数；单机演示规模足够。
/// </summary>
public sealed class ServiceStore
{
    private readonly ConcurrentDictionary<long, ServiceRecord> _rows = new();
    private readonly object _gate = new();
    private long _seq;

    public IReadOnlyList<ServiceRecord> List() => _rows.Values.OrderBy(r => r.Id).ToList();

    public ServiceRecord? Get(long id) => _rows.TryGetValue(id, out var r) ? r : null;

    /// <summary>创建一条服务。返回 (记录, 错误文本)，错误时记录为 null。</summary>
    public (ServiceRecord?, string?) Create(JsonElement body)
    {
        var name = Str(body, "name", 64);
        var port = Num(body, "port");
        if (string.IsNullOrWhiteSpace(name)) return (null, "服务名不能为空");
        if (port is null or < 1 or > 65535) return (null, "端口必须是 1-65535");
        lock (_gate)
        {
            if (_rows.Values.Any(r => r.Port == port)) return (null, $"端口 {port} 已存在");
            var row = new ServiceRecord
            {
                Id = Interlocked.Increment(ref _seq),
                Name = name,
                Port = port.Value,
                Level = Math.Clamp(Num(body, "level") ?? 0, 0, 4),
                Heartbeat = Str(body, "heartbeat", 32),
                Note = Str(body, "note", 255),
                Paused = false,
            };
            _rows[row.Id] = row;
            return (row, null);
        }
    }

    public bool Patch(long id, JsonElement patch)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row)) return false;
            if (patch.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                row.Name = Trunc(n.GetString() ?? "", 64);
            if (patch.TryGetProperty("level", out var l) && l.TryGetInt32(out var lv))
                row.Level = Math.Clamp(lv, 0, 4);
            if (patch.TryGetProperty("heartbeat", out var h) && h.ValueKind == JsonValueKind.String)
                row.Heartbeat = Trunc(h.GetString() ?? "", 32);
            if (patch.TryGetProperty("note", out var nt) && nt.ValueKind == JsonValueKind.String)
                row.Note = Trunc(nt.GetString() ?? "", 255);
            if (patch.TryGetProperty("paused", out var p) &&
                (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False))
                row.Paused = p.GetBoolean();
            return true;
        }
    }

    public bool Delete(long id) => _rows.TryRemove(id, out _);

    /// <summary>
    /// 全量覆盖式上报：同 reporter 的旧行整体删除，再逐行写入。
    /// 幂等——同一份 payload 连发两次，表内行数不变。
    /// 坏行跳过而不是整单拒绝，与 Spring 版行为一致。
    /// </summary>
    public int Replace(string reporter, JsonElement services)
    {
        if (services.ValueKind != JsonValueKind.Array) return -1;
        reporter = Trunc(reporter, 255);
        var accepted = 0;
        lock (_gate)
        {
            foreach (var old in _rows.Values.Where(r => r.Note == reporter).ToList())
                _rows.TryRemove(old.Id, out _);

            foreach (var item in services.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var port = Num(item, "port");
                if (port is null or < 1 or > 65535) continue;
                var row = new ServiceRecord
                {
                    Port = port.Value,
                    Name = Str(item, "name", 64),
                    Level = Math.Clamp(Num(item, "level") ?? 0, 0, 4),
                    Heartbeat = Str(item, "heartbeat", 32),
                    Note = reporter,
                    Paused = false,
                };
                // 端口被别的机器占则更新而非新建（全局唯一约束）
                var samePort = _rows.Values.FirstOrDefault(r => r.Port == port);
                row.Id = samePort?.Id ?? Interlocked.Increment(ref _seq);
                _rows[row.Id] = row;
                accepted++;
            }
        }
        return accepted;
    }

    public static string Str(JsonElement e, string key, int max)
    {
        if (e.ValueKind == JsonValueKind.Object &&
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return Trunc(v.GetString() ?? "", max);
        return "";
    }

    public static int? Num(JsonElement e, string key)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var m)) return m;
        return null;
    }

    public static string Trunc(string s, int max) => s.Length > max ? s[..max] : s;
}
