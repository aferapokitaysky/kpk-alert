namespace Watcher.Api.AlertSources;

public sealed class AlertStatusAggregator
{
    public AlertSourcesStatusResponse Aggregate(
        DateTimeOffset checkedAtUtc,
        IEnumerable<AlertSourceObservation> observations)
    {
        AlertSourceObservation[] snapshot = observations.ToArray();
        return new(
            checkedAtUtc,
            [
                BuildTarget(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                    AlertSourceCatalog.PoltavaOblastName, snapshot),
                BuildTarget(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
                    AlertSourceCatalog.KremenchukHromadaName, snapshot),
                BuildTarget(AlertScope.Raion, AlertSourceCatalog.KremenchukRaionId,
                    AlertSourceCatalog.KremenchukRaionName, snapshot)
            ]);
    }

    private static AlertTargetStatus BuildTarget(
        AlertScope scope,
        string scopeId,
        string scopeName,
        IReadOnlyCollection<AlertSourceObservation> allObservations)
    {
        AlertSourceObservation[] observations = allObservations
            .Where(item => item.Scope == scope && item.ScopeId == scopeId)
            .ToArray();
        AlertSourceObservation[] fresh = observations
            .Where(item => item.Freshness == AlertFreshness.Fresh && item.State != AlertState.Unknown)
            .ToArray();
        AlertSourceObservation? authoritative = fresh
            .OrderBy(item => SourcePriority(item.Source))
            .FirstOrDefault();

        AlertState[] states = fresh.Select(item => item.State).Distinct().ToArray();
        var severityConflict = states.Length == 1 &&
            states[0] == AlertState.Active &&
            fresh.Where(item => item.State == AlertState.Active && item.AlertLevel is not null)
                .Select(item => item.AlertLevel)
                .Distinct(StringComparer.Ordinal)
                .Skip(1)
                .Any();
        var stateConflict = states.Length > 1;
        AlertState targetState = severityConflict || stateConflict
            ? AlertState.Conflict
            : authoritative?.State ?? AlertState.Unknown;

        var alertLevel = targetState == AlertState.Conflict
            ? null
            : authoritative?.State == AlertState.Active
                ? authoritative.AlertLevel
                : null;
        AlertFreshness freshness = observations.Any(item => item.Freshness == AlertFreshness.Fresh)
            ? AlertFreshness.Fresh
            : observations.Any(item => item.Freshness == AlertFreshness.Stale)
                ? AlertFreshness.Stale
                : AlertFreshness.Unknown;

        return new(
            scope.ToString().ToLowerInvariant(),
            scopeId,
            scopeName,
            targetState.ToString().ToLowerInvariant(),
            alertLevel,
            freshness.ToString().ToLowerInvariant(),
            observations.MaxBy(item => item.CheckedAtUtc)?.CheckedAtUtc,
            authoritative?.DataUpdatedAtUtc,
            authoritative is null ? null : ToResponse(authoritative),
            observations.Select(ToResponse).ToArray());
    }

    private static int SourcePriority(string source) => source switch
    {
        AlertSourceCatalog.UkraineAlarmCode => 0,
        AlertSourceCatalog.NeptunCode => 1,
        AlertSourceCatalog.AlertsInUaCode => 2,
        _ => 3
    };

    private static AlertSourceObservationResponse ToResponse(AlertSourceObservation item) =>
        new(
            item.Source,
            item.SourceName,
            item.Scope.ToString().ToLowerInvariant(),
            item.ScopeId,
            item.ScopeName,
            item.State.ToString().ToLowerInvariant(),
            item.AlertLevel,
            item.Freshness.ToString().ToLowerInvariant(),
            item.CheckedAtUtc,
            item.DataUpdatedAtUtc,
            item.ErrorCode,
            item.AttributionUrl,
            item.LastKnownState?.ToString().ToLowerInvariant(),
            item.LastKnownAlertLevel);
}
