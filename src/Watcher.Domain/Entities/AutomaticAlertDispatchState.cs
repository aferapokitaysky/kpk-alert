namespace Watcher.Domain.Entities;

public sealed class AutomaticAlertDispatchState
{
    public const string KremenchukId = "kremenchuk";

    public string Id { get; set; } = KremenchukId;
    public bool HasOpenEpisode { get; set; }
    public string? Scope { get; set; }
    public string? ScopeId { get; set; }
    public Guid? StartCommandId { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public string LastDecisionCode { get; set; } = "not_evaluated";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
