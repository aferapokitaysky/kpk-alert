namespace Watcher.Api.AlertSources;

public enum AutomaticAlertDispatchAction
{
    InitializeBaseline,
    Start,
    Replace,
    Stop,
    Hold,
    NoChange
}

public sealed record OpenAlertEpisode(AlertScope Scope, string ScopeId);

public sealed record AlertObservationBaseline(
    string Source,
    string Scope,
    string ScopeId,
    string Level,
    string? AlertLevel,
    string? LastAttemptedAlertLevel,
    string? LastAttemptedAlertLevelScope,
    string? LastAttemptedAlertLevelScopeId,
    DateTimeOffset? LastAttemptedAlertLevelDataUpdatedAtUtc = null);

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
    private static readonly TimeSpan MaximumCorroborationSkew = TimeSpan.FromMinutes(2);

    public AutomaticAlertDispatchDecision Evaluate(
        AlertSourcesStatusResponse status,
        OpenAlertEpisode? openEpisode,
        AlertObservationBaseline? baseline = null)
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

        if (observations.Any(item => IsAggregateAmbiguous(targets, item.Observation)))
        {
            return Hold("observation_not_actionable");
        }

        if (openEpisode is null)
        {
            PolicyObservation? current = SelectCurrentObservation(targets, observations);
            if (current is null)
            {
                return Hold("observation_not_actionable");
            }

            if (baseline is not null &&
                string.Equals(baseline.Level, Inactive, StringComparison.Ordinal) &&
                IsState(current.Observation, Active) &&
                string.Equals(baseline.Source, selectedSource, StringComparison.Ordinal) &&
                string.Equals(baseline.Scope, current.Observation.Scope, StringComparison.Ordinal) &&
                string.Equals(baseline.ScopeId, current.Observation.ScopeId, StringComparison.Ordinal) &&
                IsStartEvidence(current))
            {
                return new(
                    AutomaticAlertDispatchAction.Start,
                    "confirmed_inactive_to_active",
                    current.Observation);
            }

            if (baseline is null || !MatchesBaseline(baseline, current.Observation))
            {
                return new(
                    AutomaticAlertDispatchAction.InitializeBaseline,
                    "observation_baseline_initialized",
                    current.Observation);
            }

            if (IsState(current.Observation, Inactive))
            {
                return new(AutomaticAlertDispatchAction.NoChange, "no_open_alert", current.Observation);
            }

            if (IsState(current.Observation, Active) &&
                IsStartEvidence(current))
            {
                var wasInactive = string.Equals(baseline.Level, Inactive, StringComparison.Ordinal);
                var levelChanged = string.Equals(baseline.Level, Active, StringComparison.Ordinal) &&
                    IsKnownColoredLevel(baseline.AlertLevel) &&
                    IsKnownColoredLevel(current.Observation.AlertLevel) &&
                    string.Equals(current.Observation.Source, TertiarySource, StringComparison.Ordinal) &&
                    string.Equals(baseline.Scope, current.Observation.Scope, StringComparison.Ordinal) &&
                    string.Equals(baseline.ScopeId, current.Observation.ScopeId, StringComparison.Ordinal) &&
                    !string.Equals(baseline.AlertLevel, current.Observation.AlertLevel, StringComparison.Ordinal);
                if (!wasInactive && !levelChanged)
                {
                    return new(AutomaticAlertDispatchAction.NoChange, "alert_baseline_active", current.Observation);
                }

                return new(
                    AutomaticAlertDispatchAction.Start,
                    levelChanged ? "alert_level_transition_active" : selectedSource switch
                    {
                        PrimarySource => "primary_alert_transition_active",
                        FallbackSource => "fallback_alert_transition_active",
                        _ => "tertiary_alert_transition_active"
                    },
                    current.Observation);
            }

            return new(AutomaticAlertDispatchAction.NoChange, "alert_baseline_active", current.Observation);
        }

        PolicyObservation[] currentEpisodeObservations = observations
            .Where(item => CoversKremenchuk(item.Observation.Scope, item.Observation.ScopeId))
            .OrderBy(item => item.Precision)
            .ToArray();
        if (currentEpisodeObservations.Length == 0)
        {
            return Hold("no_fresh_observation");
        }

        PolicyObservation? changedLevel = currentEpisodeObservations
            .Where(IsMaintainingActiveEpisode)
            .Select(item => SelectConfirmedColorObservation(targets, item.Observation))
            .Where(item => item is not null)
            .Select(item => item!)
            .Where(item => baseline is not null && IsAlertLevelChangePending(baseline, item.Observation))
            .OrderBy(item => item.Precision)
            .FirstOrDefault();
        if (changedLevel is not null)
        {
            return new(
                AutomaticAlertDispatchAction.Replace,
                "alert_level_changed",
                changedLevel.Observation);
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

    public AlertSourceObservationResponse? SelectBaselineObservation(AlertSourcesStatusResponse status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var selectedSource = SelectSource(status.Targets);
        if (selectedSource is null)
        {
            return null;
        }

        List<PolicyObservation> observations = GetPolicyObservations(status.Targets, selectedSource);
        PolicyObservation? candidate = SelectBaselineObservation(observations);
        return candidate is not null && !IsAggregateAmbiguous(status.Targets.ToArray(), candidate.Observation)
            ? candidate.Observation
            : null;
    }

    private static PolicyObservation? SelectCurrentObservation(
        IReadOnlyCollection<AlertTargetStatus> targets,
        IEnumerable<PolicyObservation> observations)
    {
        PolicyObservation? current = SelectBaselineObservation(observations);
        if (current is null || !IsState(current.Observation, Active))
        {
            return current;
        }

        PolicyObservation? confirmedColor = SelectConfirmedColorObservation(targets, current.Observation);
        if (confirmedColor is not null)
        {
            return confirmedColor;
        }

        return string.Equals(current.Observation.Source, TertiarySource, StringComparison.Ordinal) &&
               current.Observation.AlertLevel is not null
            ? current with { Observation = current.Observation with { AlertLevel = null } }
            : current;
    }

    private static PolicyObservation? SelectConfirmedColorObservation(
        IEnumerable<AlertTargetStatus> targets,
        AlertSourceObservationResponse activeObservation)
    {
        if (!IsState(activeObservation, Active) || !IsAllowedGeography(activeObservation, TertiarySource))
        {
            return null;
        }

        AlertTargetStatus? target = targets.FirstOrDefault(item =>
            string.Equals(item.Scope, activeObservation.Scope, StringComparison.Ordinal) &&
            string.Equals(item.ScopeId, activeObservation.ScopeId, StringComparison.Ordinal));
        if (target is null || IsAggregateAmbiguous([target], activeObservation) ||
            !IsState(target.AuthoritativeObservation ?? activeObservation, Active))
        {
            return null;
        }

        AlertSourceObservationResponse? color = target.Observations.FirstOrDefault(item =>
            string.Equals(item.Source, TertiarySource, StringComparison.Ordinal) &&
            string.Equals(item.Scope, activeObservation.Scope, StringComparison.Ordinal) &&
            string.Equals(item.ScopeId, activeObservation.ScopeId, StringComparison.Ordinal) &&
            IsFresh(item) && IsState(item, Active) && IsKnownColoredLevel(item.AlertLevel) &&
            HaveAlignedSourceTimestamps(item, activeObservation));
        if (color is null)
        {
            return null;
        }

        var corroborated = target.Observations.Any(item =>
            (string.Equals(item.Source, PrimarySource, StringComparison.Ordinal) ||
             string.Equals(item.Source, FallbackSource, StringComparison.Ordinal)) &&
            string.Equals(item.Scope, color.Scope, StringComparison.Ordinal) &&
            string.Equals(item.ScopeId, color.ScopeId, StringComparison.Ordinal) &&
            IsFresh(item) && IsState(item, Active) &&
            HaveAlignedSourceTimestamps(item, color));
        if (!corroborated)
        {
            return null;
        }

        if (!TryGetPrecision(color.Scope, color.ScopeId, out var precision))
        {
            return null;
        }

        return new(color, precision);
    }

    private static bool HaveAlignedSourceTimestamps(
        AlertSourceObservationResponse first,
        AlertSourceObservationResponse second) =>
        first.DataUpdatedAtUtc is { } firstUpdatedAt &&
        second.DataUpdatedAtUtc is { } secondUpdatedAt &&
        (firstUpdatedAt - secondUpdatedAt).Duration() <= MaximumCorroborationSkew;

    private static PolicyObservation? SelectBaselineObservation(IEnumerable<PolicyObservation> observations) =>
        observations
            .Where(item => IsState(item.Observation, Active) || IsState(item.Observation, Inactive))
            .Where(item => !IsAmbiguous(item))
            .OrderBy(item => item.Precision)
            .FirstOrDefault();

    private static bool MatchesBaseline(
        AlertObservationBaseline baseline,
        AlertSourceObservationResponse observation) =>
        string.Equals(baseline.Source, observation.Source, StringComparison.Ordinal) &&
        string.Equals(baseline.Scope, observation.Scope, StringComparison.Ordinal) &&
        string.Equals(baseline.ScopeId, observation.ScopeId, StringComparison.Ordinal);

    private static bool IsKnownColoredLevel(string? level) =>
        string.Equals(level, Red, StringComparison.Ordinal) ||
        string.Equals(level, Yellow, StringComparison.Ordinal);

    private static bool IsAggregateAmbiguous(
        IEnumerable<AlertTargetStatus> targets,
        AlertSourceObservationResponse observation)
    {
        AlertTargetStatus? target = targets.FirstOrDefault(item =>
            string.Equals(item.Scope, observation.Scope, StringComparison.Ordinal) &&
            string.Equals(item.ScopeId, observation.ScopeId, StringComparison.Ordinal));
        return target is null ||
               !string.Equals(target.Freshness, Fresh, StringComparison.Ordinal) ||
               target.Level is Partial or Conflict or Unknown;
    }

    private static bool IsAlertLevelChangePending(
        AlertObservationBaseline baseline,
        AlertSourceObservationResponse observation) =>
        string.Equals(observation.Source, TertiarySource, StringComparison.Ordinal) &&
        string.Equals(observation.Level, Active, StringComparison.Ordinal) &&
        IsKnownColoredLevel(observation.AlertLevel) &&
        string.Equals(baseline.LastAttemptedAlertLevelScope, observation.Scope, StringComparison.Ordinal) &&
        string.Equals(baseline.LastAttemptedAlertLevelScopeId, observation.ScopeId, StringComparison.Ordinal) &&
        observation.DataUpdatedAtUtc is { } observedAtUtc &&
        (baseline.LastAttemptedAlertLevelDataUpdatedAtUtc is not { } lastAppliedAtUtc ||
         observedAtUtc > lastAppliedAtUtc) &&
        !string.Equals(baseline.LastAttemptedAlertLevel, observation.AlertLevel, StringComparison.Ordinal);

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
                IsTertiaryGeography(item.Observation) &&
                (item.Observation.AlertLevel is null || IsKnownColoredLevel(item.Observation.AlertLevel)));
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
                IsTertiaryGeography(item.Observation) &&
                (item.Observation.AlertLevel is null || IsKnownColoredLevel(item.Observation.AlertLevel)));
    }

    private static bool IsAmbiguous(PolicyObservation item) =>
        IsState(item.Observation, Partial) ||
        IsState(item.Observation, Conflict) ||
        IsState(item.Observation, Unknown) ||
        HasUnexpectedAlertLevel(item) ||
        (IsState(item.Observation, Active) &&
         string.Equals(item.Observation.Source, TertiarySource, StringComparison.Ordinal) &&
         item.Observation.AlertLevel is not null &&
         !IsKnownColoredLevel(item.Observation.AlertLevel));

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
