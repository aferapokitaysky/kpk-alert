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
    private const string PrimarySource = AlertSourceCatalog.UkraineAlarmCode;
    private const string FallbackSource = AlertSourceCatalog.NeptunCode;
    private const string TertiarySource = AlertSourceCatalog.AlertsInUaCode;
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
        var selectedSource = SelectSource(targets);
        if (selectedSource is null)
        {
            return Hold("no_fresh_observation");
        }

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
                    selectedSource switch
                    {
                        PrimarySource => "primary_alert_active",
                        FallbackSource => "fallback_alert_active",
                        _ => "tertiary_alert_active"
                    },
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

            PolicyObservation[] morePrecise = currentEpisodeObservations
                .Where(item => item.Precision < broadestAvailable.Precision)
                .ToArray();
            if (morePrecise.Any(IsAmbiguous))
            {
                return Hold("observation_not_actionable");
            }

            if (morePrecise.Any(IsMaintainingActiveEpisode))
            {
                PolicyObservation activeObservation = morePrecise.First(IsMaintainingActiveEpisode);
                return new(
                    AutomaticAlertDispatchAction.NoChange,
                    "alert_still_active",
                    activeObservation.Observation);
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

                result.Add(new(observation, precision));
            }
        }

        return result;
    }

    private static bool IsAllowedGeography(
        AlertSourceObservationResponse observation,
        string selectedSource) => selectedSource switch
        {
            PrimarySource => IsPrimaryGeography(observation),
            FallbackSource => IsFallbackGeography(observation),
            TertiarySource => IsTertiaryGeography(observation),
            _ => false
        };

    private static string? SelectSource(IEnumerable<AlertTargetStatus> targets)
    {
        foreach (var source in new[] { PrimarySource, FallbackSource, TertiarySource })
        {
            var available = targets
                .SelectMany(target => target.Observations)
                .Any(observation =>
                    string.Equals(observation.Source, source, StringComparison.Ordinal) &&
                    IsFresh(observation) &&
                    !IsState(observation, Unknown) &&
                    TryGetPrecision(observation.Scope, observation.ScopeId, out _) &&
                    IsAllowedGeography(observation, source));
            if (available)
            {
                return source;
            }
        }

        return null;
    }

    private static bool IsStartEvidence(PolicyObservation item)
    {
        if (!IsState(item.Observation, Active))
        {
            return false;
        }

        if (string.Equals(item.Observation.Source, PrimarySource, StringComparison.Ordinal))
        {
            return IsPrimaryGeography(item.Observation) && item.Observation.AlertLevel is null;
        }

        return IsActiveWithoutColor(item.Observation) ||
               (string.Equals(item.Observation.Source, TertiarySource, StringComparison.Ordinal) &&
                IsTertiaryGeography(item.Observation) && IsLevel(item.Observation, Red));
    }

    private static bool IsMaintainingActiveEpisode(PolicyObservation item)
    {
        if (!IsState(item.Observation, Active))
        {
            return false;
        }

        if (string.Equals(item.Observation.Source, PrimarySource, StringComparison.Ordinal))
        {
            return IsPrimaryGeography(item.Observation) && item.Observation.AlertLevel is null;
        }

        return IsActiveWithoutColor(item.Observation) ||
               (string.Equals(item.Observation.Source, TertiarySource, StringComparison.Ordinal) &&
                IsTertiaryGeography(item.Observation) && IsLevel(item.Observation, Red));
    }

    private static bool IsAmbiguous(PolicyObservation item) =>
        IsState(item.Observation, Partial) ||
        IsState(item.Observation, Conflict) ||
        IsState(item.Observation, Unknown) ||
        IsLevel(item.Observation, Yellow) ||
        HasUnexpectedAlertLevel(item) ||
        (IsState(item.Observation, Active) &&
         string.Equals(item.Observation.Source, TertiarySource, StringComparison.Ordinal) &&
         !IsLevel(item.Observation, Red));

    private static bool IsActiveWithoutColor(AlertSourceObservationResponse observation) =>
        (string.Equals(observation.Source, PrimarySource, StringComparison.Ordinal) ||
         string.Equals(observation.Source, FallbackSource, StringComparison.Ordinal)) &&
        observation.AlertLevel is null;

    private static bool HasUnexpectedAlertLevel(PolicyObservation item)
    {
        if (item.Observation.AlertLevel is null)
        {
            return false;
        }

        return !string.Equals(item.Observation.Source, TertiarySource, StringComparison.Ordinal) ||
               !IsState(item.Observation, Active) ||
               (!IsLevel(item.Observation, Red) && !IsLevel(item.Observation, Yellow));
    }

    private static bool IsFresh(AlertSourceObservationResponse observation) =>
        string.Equals(observation.Freshness, Fresh, StringComparison.Ordinal);

    private static bool IsState(AlertSourceObservationResponse observation, string value) =>
        string.Equals(observation.Level, value, StringComparison.Ordinal);

    private static bool IsLevel(AlertSourceObservationResponse observation, string value) =>
        string.Equals(observation.AlertLevel, value, StringComparison.Ordinal);

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

    private static bool IsTertiaryGeography(AlertSourceObservationResponse observation) =>
        IsPrimaryGeography(observation) || IsFallbackGeography(observation);

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
        int Precision);
}
