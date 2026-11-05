namespace Watcher.Api.AlertSources;

public enum AutomaticAlertDispatchAction
{
    Start,
    Stop,
    Hold,
    NoChange
}

public sealed record OpenAlertEpisode(AlertScope Scope, string ScopeId);

public sealed record AutomaticAlertDispatchDecision(
    AutomaticAlertDispatchAction Action,
    string ReasonCode,
    AlertSourceObservationResponse? Observation);

public sealed class AutomaticAlertDispatchPolicy
{
    private const string PrimarySource = AlertSourceCatalog.AlertsInUaCode;
    private const string FallbackSource = AlertSourceCatalog.NeptunCode;
    private const string Fresh = "fresh";
    private const string Active = "active";
    private const string Inactive = "inactive";
    private const string Partial = "partial";
    private const string Unknown = "unknown";
    private const string Conflict = "conflict";
    private const string Red = "red";
    private const string Yellow = "yellow";

    public AutomaticAlertDispatchDecision Evaluate(
        AlertSourcesStatusResponse status,
        OpenAlertEpisode? openEpisode)
    {
        ArgumentNullException.ThrowIfNull(status);

        if (openEpisode is not null && !TryGetPrecision(openEpisode.Scope, openEpisode.ScopeId, out _))
        {
            return Hold("invalid_open_episode");
        }

        AlertTargetStatus[] targets = status.Targets.ToArray();
        AlertTargetStatus[] primaryTargets = targets
            .Where(IsPrimaryGeography)
            .ToArray();
        var primaryAvailable = primaryTargets
            .SelectMany(target => target.Observations)
            .Any(observation =>
                string.Equals(observation.Source, PrimarySource, StringComparison.Ordinal) &&
                IsPrimaryGeography(observation) &&
                IsFresh(observation) &&
                !IsState(observation, Unknown));

        var selectedSource = primaryAvailable ? PrimarySource : FallbackSource;
        List<PolicyObservation> observations = GetPolicyObservations(targets, selectedSource);
        if (observations.Count == 0)
        {
            return Hold("no_fresh_observation");
        }

        if (openEpisode is null)
        {
            PolicyObservation? startCandidate = observations
                .Where(IsStartEvidence)
                .OrderBy(item => item.Precision)
                .FirstOrDefault();
            if (startCandidate is not null)
            {
                return new(
                    AutomaticAlertDispatchAction.Start,
                    selectedSource == PrimarySource ? "primary_alert_active" : "fallback_alert_active",
                    startCandidate.Observation);
            }

            if (observations.Any(IsAmbiguous))
            {
                return Hold("observation_not_actionable");
            }

            if (observations.Any(item => IsState(item.Observation, Inactive)))
            {
                return new(AutomaticAlertDispatchAction.NoChange, "no_open_alert", null);
            }

            return Hold("observation_not_actionable");
        }

        PolicyObservation[] currentEpisodeObservations = observations
            .Where(item => CoversKremenchuk(item.Observation.Scope, item.Observation.ScopeId))
            .OrderBy(item => item.Precision)
            .ToArray();
        if (currentEpisodeObservations.Length == 0)
        {
            return Hold("no_fresh_observation");
        }

        var episodePrecision = GetPrecision(openEpisode.Scope);
        PolicyObservation[] sameOrBroader = currentEpisodeObservations
            .Where(item => item.Precision >= episodePrecision)
            .OrderByDescending(item => item.Precision)
            .ToArray();
        if (sameOrBroader.Length > 0)
        {
            PolicyObservation broadestAvailable = sameOrBroader[0];
            PolicyObservation[] equallyBroad = sameOrBroader
                .Where(item => item.Precision == broadestAvailable.Precision)
                .ToArray();

            if (equallyBroad.Any(IsAmbiguous))
            {
                return Hold("observation_not_actionable");
            }

            if (equallyBroad.Any(item => IsState(item.Observation, Inactive)))
            {
                PolicyObservation inactiveObservation = equallyBroad
                    .First(item => IsState(item.Observation, Inactive));
                return new(
                    AutomaticAlertDispatchAction.Stop,
                    "confirmed_inactive",
                    inactiveObservation.Observation);
            }

            if (equallyBroad.Any(IsMaintainingActiveEpisode))
            {
                PolicyObservation activeObservation = equallyBroad.First(IsMaintainingActiveEpisode);
                return new(
                    AutomaticAlertDispatchAction.NoChange,
                    "alert_still_active",
                    activeObservation.Observation);
            }
        }

        if (currentEpisodeObservations.Any(IsMaintainingActiveEpisode))
        {
            PolicyObservation activeObservation = currentEpisodeObservations
                .First(IsMaintainingActiveEpisode);
            return new(
                AutomaticAlertDispatchAction.NoChange,
                "alert_still_active",
                activeObservation.Observation);
        }

        return Hold("awaiting_geographic_clear");
    }

    private static List<PolicyObservation> GetPolicyObservations(
        IEnumerable<AlertTargetStatus> targets,
        string selectedSource)
    {
        List<PolicyObservation> result = [];
        foreach (AlertTargetStatus target in targets)
        {
            foreach (AlertSourceObservationResponse observation in target.Observations)
            {
                if (!string.Equals(observation.Source, selectedSource, StringComparison.Ordinal) ||
                    !string.Equals(target.Scope, observation.Scope, StringComparison.Ordinal) ||
                    !string.Equals(target.ScopeId, observation.ScopeId, StringComparison.Ordinal) ||
                    !IsFresh(observation) ||
                    !IsAllowedGeography(observation, selectedSource) ||
                    !TryGetPrecision(observation.Scope, observation.ScopeId, out var precision))
                {
                    continue;
                }

                var targetConflicts = IsState(target, Conflict);
                result.Add(new(observation, precision, targetConflicts));
            }
        }

        return result;
    }

    private static bool IsAllowedGeography(
        AlertSourceObservationResponse observation,
        string selectedSource) =>
        string.Equals(selectedSource, PrimarySource, StringComparison.Ordinal)
            ? IsPrimaryGeography(observation)
            : IsFallbackGeography(observation);

    private static bool IsStartEvidence(PolicyObservation item)
    {
        if ((item.TargetConflicts && !IsAuthoritativePrimaryRed(item.Observation)) ||
            !IsState(item.Observation, Active))
        {
            return false;
        }

        if (string.Equals(item.Observation.Source, PrimarySource, StringComparison.Ordinal))
        {
            return IsPrimaryGeography(item.Observation) && IsLevel(item.Observation, Red);
        }

        return string.Equals(item.Observation.Source, FallbackSource, StringComparison.Ordinal) &&
               IsFallbackGeography(item.Observation) &&
               item.Observation.AlertLevel is null;
    }

    private static bool IsMaintainingActiveEpisode(PolicyObservation item)
    {
        if ((item.TargetConflicts && !IsAuthoritativePrimaryRed(item.Observation)) ||
            !IsState(item.Observation, Active))
        {
            return false;
        }

        if (string.Equals(item.Observation.Source, PrimarySource, StringComparison.Ordinal))
        {
            return IsPrimaryGeography(item.Observation) && IsLevel(item.Observation, Red);
        }

        return string.Equals(item.Observation.Source, FallbackSource, StringComparison.Ordinal) &&
               IsFallbackGeography(item.Observation) &&
               item.Observation.AlertLevel is null;
    }

    private static bool IsAmbiguous(PolicyObservation item) =>
        (item.TargetConflicts && !IsAuthoritativePrimaryRed(item.Observation)) ||
        IsState(item.Observation, Partial) ||
        IsState(item.Observation, Conflict) ||
        IsState(item.Observation, Unknown) ||
        IsLevel(item.Observation, Yellow) ||
        HasUnexpectedAlertLevel(item) ||
        (IsState(item.Observation, Active) &&
         string.Equals(item.Observation.Source, PrimarySource, StringComparison.Ordinal) &&
         !IsLevel(item.Observation, Red));

    private static bool HasUnexpectedAlertLevel(PolicyObservation item)
    {
        if (item.Observation.AlertLevel is null)
        {
            return false;
        }

        return !string.Equals(item.Observation.Source, PrimarySource, StringComparison.Ordinal) ||
               !IsState(item.Observation, Active) ||
               (!IsLevel(item.Observation, Red) && !IsLevel(item.Observation, Yellow));
    }

    private static bool IsFresh(AlertSourceObservationResponse observation) =>
        string.Equals(observation.Freshness, Fresh, StringComparison.Ordinal);

    private static bool IsAuthoritativePrimaryRed(AlertSourceObservationResponse observation) =>
        string.Equals(observation.Source, PrimarySource, StringComparison.Ordinal) &&
        IsPrimaryGeography(observation) &&
        IsState(observation, Active) &&
        IsLevel(observation, Red);

    private static bool IsState(AlertSourceObservationResponse observation, string value) =>
        string.Equals(observation.Level, value, StringComparison.Ordinal);

    private static bool IsState(AlertTargetStatus target, string value) =>
        string.Equals(target.Level, value, StringComparison.Ordinal);

    private static bool IsLevel(AlertSourceObservationResponse observation, string value) =>
        string.Equals(observation.AlertLevel, value, StringComparison.Ordinal);

    private static bool IsPrimaryGeography(AlertTargetStatus target) =>
        (string.Equals(target.Scope, "hromada", StringComparison.Ordinal) &&
         string.Equals(target.ScopeId, AlertSourceCatalog.KremenchukHromadaId, StringComparison.Ordinal)) ||
        (string.Equals(target.Scope, "oblast", StringComparison.Ordinal) &&
         string.Equals(target.ScopeId, AlertSourceCatalog.PoltavaOblastId, StringComparison.Ordinal));

    private static bool IsPrimaryGeography(AlertSourceObservationResponse observation) =>
        IsPrimaryGeography(observation.Scope, observation.ScopeId);

    private static bool IsPrimaryGeography(string scope, string scopeId) =>
        (string.Equals(scope, "hromada", StringComparison.Ordinal) &&
         string.Equals(scopeId, AlertSourceCatalog.KremenchukHromadaId, StringComparison.Ordinal)) ||
        (string.Equals(scope, "oblast", StringComparison.Ordinal) &&
         string.Equals(scopeId, AlertSourceCatalog.PoltavaOblastId, StringComparison.Ordinal));

    private static bool IsFallbackGeography(AlertSourceObservationResponse observation) =>
        (string.Equals(observation.Scope, "oblast", StringComparison.Ordinal) &&
         string.Equals(observation.ScopeId, AlertSourceCatalog.PoltavaOblastId, StringComparison.Ordinal)) ||
        (string.Equals(observation.Scope, "raion", StringComparison.Ordinal) &&
         string.Equals(observation.ScopeId, AlertSourceCatalog.KremenchukRaionId, StringComparison.Ordinal));

    private static bool CoversKremenchuk(string scope, string scopeId) =>
        IsPrimaryGeography(scope, scopeId) ||
        (string.Equals(scope, "raion", StringComparison.Ordinal) &&
         string.Equals(scopeId, AlertSourceCatalog.KremenchukRaionId, StringComparison.Ordinal));

    private static bool TryGetPrecision(AlertScope scope, string scopeId, out int precision)
    {
        var scopeName = scope.ToString().ToLowerInvariant();
        return TryGetPrecision(scopeName, scopeId, out precision);
    }

    private static bool TryGetPrecision(string scope, string scopeId, out int precision)
    {
        if (string.Equals(scope, "hromada", StringComparison.Ordinal) &&
            string.Equals(scopeId, AlertSourceCatalog.KremenchukHromadaId, StringComparison.Ordinal))
        {
            precision = 0;
            return true;
        }

        if (string.Equals(scope, "raion", StringComparison.Ordinal) &&
            string.Equals(scopeId, AlertSourceCatalog.KremenchukRaionId, StringComparison.Ordinal))
        {
            precision = 1;
            return true;
        }

        if (string.Equals(scope, "oblast", StringComparison.Ordinal) &&
            string.Equals(scopeId, AlertSourceCatalog.PoltavaOblastId, StringComparison.Ordinal))
        {
            precision = 2;
            return true;
        }

        precision = int.MaxValue;
        return false;
    }

    private static int GetPrecision(AlertScope scope) => scope switch
    {
        AlertScope.Hromada => 0,
        AlertScope.Raion => 1,
        AlertScope.Oblast => 2,
        _ => int.MaxValue
    };

    private static AutomaticAlertDispatchDecision Hold(string reasonCode) =>
        new(AutomaticAlertDispatchAction.Hold, reasonCode, null);

    private sealed record PolicyObservation(
        AlertSourceObservationResponse Observation,
        int Precision,
        bool TargetConflicts);
}
