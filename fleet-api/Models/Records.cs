namespace FleetApi.Models;

/// <summary>一台受管服务。字段名与 Spring 版 ServiceEntity 对齐，前端无需改动。</summary>
public sealed class ServiceRecord
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public int Port { get; set; }
    public int Level { get; set; }
    public string? Heartbeat { get; set; }
    public string? Note { get; set; }
    public bool Paused { get; set; }
}

/// <summary>一条预约单。字段名与 Spring 版 OrderEntity 对齐。</summary>
public sealed class OrderRecord
{
    public const string New = "NEW";
    public const string Accepted = "ACCEPTED";
    public const string Done = "DONE";
    public const string Cancelled = "CANCELLED";

    public long Id { get; set; }
    public string CustomerName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Service { get; set; } = "";
    public string? PreferredAt { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = New;
    public string? Source { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed record ErrorDto(string Error);

public sealed class RejectedException : Exception
{
    public string Kind { get; }
    public RejectedException(string kind, string message) : base(message) => Kind = kind;
}
