namespace Watcher.Api.AlertSources;

public enum AlertState
{
    Active,
    Inactive,
    Partial,
    Unknown,
    Conflict
}

public enum AlertScope
{
    Oblast,
    Raion,
    Hromada
}

public enum AlertFreshness
{
    Fresh,
    Stale,
    Unknown
}

public sealed record AlertSourceObservation(
    string Source,
    string SourceName,
    AlertScope Scope,
    string ScopeId,
    string ScopeName,
    AlertState State,
    string? AlertLevel,
    AlertFreshness Freshness,
    DateTimeOffset? CheckedAtUtc,
    DateTimeOffset? DataUpdatedAtUtc,
    string? ErrorCode,
    string AttributionUrl,
    AlertState? LastKnownState = null,
    string? LastKnownAlertLevel = null);

public sealed record AlertTargetStatus(
    string Scope,
    string ScopeId,
    string ScopeName,
    string Level,
    string? AlertLevel,
    string Freshness,
    DateTimeOffset? CheckedAtUtc,
    DateTimeOffset? DataUpdatedAtUtc,
    AlertSourceObservationResponse? AuthoritativeObservation,
    IReadOnlyList<AlertSourceObservationResponse> Observations);

public sealed record AlertSourceObservationResponse(
    string Source,
    string SourceName,
    string Scope,
    string ScopeId,
    string ScopeName,
    string Level,
    string? AlertLevel,
    string Freshness,
    DateTimeOffset? CheckedAtUtc,
    DateTimeOffset? DataUpdatedAtUtc,
    string? ErrorCode,
    string AttributionUrl,
    string? LastKnownLevel,
    string? LastKnownAlertLevel);

public sealed record AlertSourcesStatusResponse(
    DateTimeOffset CheckedAtUtc,
    IReadOnlyList<AlertTargetStatus> Targets);

public interface IAlertSourceProvider
{
    Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken);

    IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
        DateTimeOffset checkedAtUtc,
        string errorCode);
}
