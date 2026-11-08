namespace Watcher.Domain.Entities;

public sealed class AlertSourceObservationState
{
    public string Source { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string ScopeId { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string? AlertLevel { get; set; }
    public string Freshness { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public DateTimeOffset? CheckedAtUtc { get; set; }
    public DateTimeOffset? DataUpdatedAtUtc { get; set; }
    public string? LastKnownLevel { get; set; }
    public string? LastKnownAlertLevel { get; set; }
}
