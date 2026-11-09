using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using FleetApi.Models;

namespace FleetApi.Data;

/// <summary>
/// 订单表 + 业务规则（限流、去重、状态机），语义对齐 Spring 的 OrderService。
/// 状态只允许 NEW -> ACCEPTED -> DONE，或任意态 -> CANCELLED。
/// </summary>
public sealed partial class OrderStore
{
    private sealed class Window
    {
        public DateTime Start;
        public int Used;
    }

    private readonly ConcurrentDictionary<long, OrderRecord> _rows = new();
    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private readonly object _gate = new();
    private readonly int _rateLimit;
    private readonly int _dedupeSeconds;
    private long _seq;

    public OrderStore(int rateLimit, int dedupeSeconds)
    {
        _rateLimit = rateLimit;
        _dedupeSeconds = dedupeSeconds;
    }

    public OrderRecord Create(JsonElement body, string clientKey)
    {
        CheckRate(clientKey);
        var name = ServiceStore.Trunc(ServiceStore.Str(body, "customerName", 64).Trim(), 64);
        var phone = ServiceStore.Str(body, "phone", 32);
        var service = ServiceStore.Str(body, "service", 64);
        if (string.IsNullOrWhiteSpace(name)) throw new RejectedException("bad-request", "联系人不能为空");
        if (!PhoneRegex().IsMatch(phone)) throw new RejectedException("bad-request", "手机号格式不对");
        if (string.IsNullOrWhiteSpace(service)) throw new RejectedException("bad-request", "预约项目不能为空");

        lock (_gate)
        {
            var since = DateTime.UtcNow.AddSeconds(-_dedupeSeconds);
            // O(n) 线性扫；数据量小时比引入索引更简单，换成数据库时改成分页查询
            var dup = _rows.Values.FirstOrDefault(r =>
                r.Phone == phone && r.Service == service && r.CreatedAt > since);
            if (dup is not null)
                throw new RejectedException("duplicate",
                    $"同一手机号在 {_dedupeSeconds} 秒内已提交过同一项目（单号 {dup.Id}）");

            // 客户端传来的 id/status/createdAt 一律不采信
            var row = new OrderRecord
            {
                Id = Interlocked.Increment(ref _seq),
                CustomerName = name,
                Phone = phone,
                Service = service,
                PreferredAt = ServiceStore.Str(body, "preferredAt", 32),
                Note = ServiceStore.Str(body, "note", 255),
                Status = OrderRecord.New,
                Source = ServiceStore.Str(body, "source", 32),
                CreatedAt = DateTime.UtcNow,
            };
            _rows[row.Id] = row;
            return row;
        }
    }

    public IReadOnlyList<OrderRecord> List(string? status, string? source) =>
        _rows.Values
            .Where(r => string.IsNullOrEmpty(status) || r.Status == status)
            .Where(r => string.IsNullOrEmpty(source) || r.Source == source)
            .OrderByDescending(r => r.CreatedAt)
            .ToList();

    public Dictionary<string, long> Stats()
    {
        var counts = new Dictionary<string, long>
        {
            [OrderRecord.New] = 0,
            [OrderRecord.Accepted] = 0,
            [OrderRecord.Done] = 0,
            [OrderRecord.Cancelled] = 0,
        };
        lock (_gate)
        {
            foreach (var r in _rows.Values)
            {
                if (counts.ContainsKey(r.Status)) counts[r.Status]++;
                else counts[r.Status] = 1;
            }
            counts["TOTAL"] = _rows.Count;
        }
        return counts;
    }

    public OrderRecord ChangeStatus(long id, string next)
    {
        if (next is not (OrderRecord.Accepted or OrderRecord.Done or OrderRecord.Cancelled))
            throw new RejectedException("bad-status", "目标状态必须是 ACCEPTED / DONE / CANCELLED");
        lock (_gate)
        {
            if (!_rows.TryGetValue(id, out var row))
                throw new RejectedException("not-found", $"单号 {id} 不存在");
            var legal = next == OrderRecord.Cancelled
                || (row.Status == OrderRecord.New && next == OrderRecord.Accepted)
                || (row.Status == OrderRecord.Accepted && next == OrderRecord.Done);
            if (!legal)
                throw new RejectedException("bad-transition", $"不允许 {row.Status} -> {next}");
            row.Status = next;
            return row;
        }
    }

    /// <summary>固定窗口限流：每 clientKey 每分钟 _rateLimit 次。</summary>
    private void CheckRate(string clientKey)
    {
        var now = DateTime.UtcNow;
        lock (_gate)
        {
            if (!_windows.TryGetValue(clientKey, out var w) || w.Start < now.AddMinutes(-1))
            {
                _windows[clientKey] = new Window { Start = now, Used = 1 };
                return;
            }
            if (++w.Used > _rateLimit)
                throw new RejectedException("rate-limited", $"提交过于频繁，每分钟最多 {_rateLimit} 单");
            if (_windows.Count > 1024)
            {
                foreach (var kv in _windows.Where(kv => kv.Value.Start < now.AddMinutes(-2)).ToList())
                    _windows.TryRemove(kv.Key, out _);
            }
        }
    }

    [GeneratedRegex(@"^1[3-9]\d{9}$", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();
}
