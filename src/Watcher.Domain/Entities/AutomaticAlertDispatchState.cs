namespace Watcher.Domain.Entities;

public sealed class AutomaticAlertDispatchState
{
    public const string KremenchukId = "kremenchuk";

    public string Id { get; set; } = KremenchukId;
    public bool HasOpenEpisode { get; set; }
    public bool HasObservationBaseline { get; set; }
    public string? LastObservedSource { get; set; }
    public string? LastObservedScope { get; set; }
    public string? LastObservedScopeId { get; set; }
    public string? LastObservedLevel { get; set; }
    public string? LastObservedAlertLevel { get; set; }
    public bool NeedsObservationResync { get; set; }
    public string? LastAttemptedAlertLevel { get; set; }
    public string? LastAttemptedAlertLevelScope { get; set; }
    public string? LastAttemptedAlertLevelScopeId { get; set; }
    public string? Scope { get; set; }
    public string? ScopeId { get; set; }
    public Guid? StartCommandId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public string LastDecisionCode { get; set; } = "not_evaluated";
    public string? LastHoldAuditFingerprint { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
