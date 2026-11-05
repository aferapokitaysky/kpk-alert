using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class AutomaticAlertDispatchPolicyTests
{
    private static readonly DateTimeOffset CheckedAtUtc = new(2026, 11, 5, 8, 0, 0, TimeSpan.Zero);
    private readonly AutomaticAlertDispatchPolicy _policy = new();

    [Fact]
    public void Fresh_red_alert_for_kremenchuk_hromada_starts_once()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "active", "red");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(Status(Target(observation)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Start, decision.Action);
        Assert.Equal("primary_alert_active", decision.ReasonCode);
        Assert.Equal(observation, decision.Observation);
    }

    [Fact]
    public void Fresh_red_alert_for_poltava_oblast_can_start()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "active", "red");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(Status(Target(observation)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Start, decision.Action);
        Assert.Equal(observation, decision.Observation);
    }

    [Fact]
    public void Fresh_active_neptun_raion_starts_when_primary_has_no_information()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "unknown");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "active");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(fallback), Target(primary)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Start, decision.Action);
        Assert.Equal("fallback_alert_active", decision.ReasonCode);
        Assert.Equal(fallback, decision.Observation);
    }

    [Fact]
    public void Fresh_active_neptun_oblast_starts_when_primary_is_stale()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "active", "red", freshness: "stale");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "active");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(fallback)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Start, decision.Action);
        Assert.Equal(fallback, decision.Observation);
    }

    [Theory]
    [InlineData("yellow")]
    [InlineData("red")]
    public void Unexpected_neptun_alert_level_does_not_start(string alertLevel)
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "unknown");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "active", alertLevel);

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(fallback)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Theory]
    [InlineData("active", "yellow", "fresh")]
    [InlineData("partial", null, "fresh")]
    [InlineData("active", "red", "stale")]
    [InlineData("unknown", null, "fresh")]
    public void Non_actionable_primary_state_does_not_start_or_use_fallback(
        string state,
        string? alertLevel,
        string freshness)
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            state, alertLevel, freshness);
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "active");
        AlertTargetStatus target = Target(primary, fallback, aggregateLevel: "conflict");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(Status(target), null);

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Fact]
    public void Fresh_primary_partial_status_blocks_neptun_fallback()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "partial");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "active");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(fallback)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Theory]
    [InlineData("active", "yellow", "fresh", "active")]
    [InlineData("partial", null, "fresh", "partial")]
    [InlineData("unknown", null, "fresh", "unknown")]
    [InlineData("active", "red", "stale", "unknown")]
    public void Ambiguous_status_never_clears_an_open_episode(
        string state,
        string? alertLevel,
        string freshness,
        string aggregateLevel)
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            state, alertLevel, freshness);

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(observation, aggregateLevel: aggregateLevel)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Fact]
    public void Conflicting_target_does_not_clear_an_open_episode()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "inactive");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "active");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary, fallback, aggregateLevel: "conflict")),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Fact]
    public void Inconsistent_primary_alert_level_does_not_clear_an_open_episode()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "inactive", "red");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(observation)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Fact]
    public void Primary_red_alert_starts_despite_inactive_neptun_fallback()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "active", "red");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary, fallback, aggregateLevel: "conflict")), null);

        Assert.Equal(AutomaticAlertDispatchAction.Start, decision.Action);
        Assert.Equal("primary_alert_active", decision.ReasonCode);
        Assert.Equal(primary, decision.Observation);
    }

    [Fact]
    public void Primary_inactive_conflict_does_not_fall_through_to_active_neptun()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "inactive");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "active");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary, fallback, aggregateLevel: "conflict")), null);

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Fact]
    public void Unknown_or_unrecognized_primary_geography_cannot_start()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", "9999", "active", "red");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(Status(Target(observation)), null);

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
    }

    [Fact]
    public void Repeated_active_status_for_an_open_episode_is_no_change()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "active", "red");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(observation)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.NoChange, decision.Action);
        Assert.Equal("alert_still_active", decision.ReasonCode);
    }

    [Fact]
    public void Switching_from_primary_to_active_fallback_does_not_create_a_second_start()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "unknown");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "active");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(fallback)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.NoChange, decision.Action);
        Assert.Equal(fallback, decision.Observation);
    }

    [Fact]
    public void Fresh_inactive_primary_status_for_the_same_geography_stops_episode()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(observation)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.Stop, decision.Action);
        Assert.Equal("confirmed_inactive", decision.ReasonCode);
        Assert.Equal(observation, decision.Observation);
    }

    [Fact]
    public void Narrower_hromada_inactive_status_does_not_stop_oblast_episode()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(observation)),
            new OpenAlertEpisode(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId));

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
        Assert.Equal("awaiting_geographic_clear", decision.ReasonCode);
    }

    [Fact]
    public void Narrower_neptun_raion_inactive_status_does_not_stop_oblast_episode()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "unknown");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(fallback)),
            new OpenAlertEpisode(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId));

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
        Assert.Equal("awaiting_geographic_clear", decision.ReasonCode);
    }

    [Theory]
    [InlineData("raion", "poltavska:kremenchutskyi")]
    [InlineData("oblast", "19")]
    public void Broader_neptun_inactive_status_can_stop_hromada_episode(string scope, string scopeId)
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "unknown");
        AlertSourceObservationResponse fallback = Observation(
            AlertSourceCatalog.NeptunCode, scope, scopeId, "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(fallback)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.Stop, decision.Action);
        Assert.Equal("confirmed_inactive", decision.ReasonCode);
        Assert.Equal(fallback, decision.Observation);
    }

    [Fact]
    public void Broader_active_status_prevents_a_narrower_inactive_status_from_stopping_episode()
    {
        AlertSourceObservationResponse hromada = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "inactive");
        AlertSourceObservationResponse oblast = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "active", "red");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(hromada), Target(oblast)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.NoChange, decision.Action);
        Assert.Equal("alert_still_active", decision.ReasonCode);
        Assert.Equal(oblast, decision.Observation);
    }

    [Fact]
    public void Broader_inactive_neptun_status_closes_hromada_episode_without_hromada_record()
    {
        AlertSourceObservationResponse primary = Observation(
            AlertSourceCatalog.AlertsInUaCode, "hromada", AlertSourceCatalog.KremenchukHromadaId,
            "unknown");
        AlertSourceObservationResponse raion = Observation(
            AlertSourceCatalog.NeptunCode, "raion", AlertSourceCatalog.KremenchukRaionId,
            "inactive");
        AlertSourceObservationResponse oblast = Observation(
            AlertSourceCatalog.NeptunCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(primary), Target(raion), Target(oblast)),
            new OpenAlertEpisode(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId));

        Assert.Equal(AutomaticAlertDispatchAction.Stop, decision.Action);
        Assert.Equal("confirmed_inactive", decision.ReasonCode);
        Assert.Equal(oblast, decision.Observation);
    }

    [Fact]
    public void Repeated_inactive_status_without_an_open_episode_is_no_change()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(Status(Target(observation)), null);

        Assert.Equal(AutomaticAlertDispatchAction.NoChange, decision.Action);
        Assert.Equal("no_open_alert", decision.ReasonCode);
    }

    [Fact]
    public void Invalid_open_episode_is_held_without_a_stop()
    {
        AlertSourceObservationResponse observation = Observation(
            AlertSourceCatalog.AlertsInUaCode, "oblast", AlertSourceCatalog.PoltavaOblastId,
            "inactive");

        AutomaticAlertDispatchDecision decision = _policy.Evaluate(
            Status(Target(observation)),
            new OpenAlertEpisode(AlertScope.Hromada, "9999"));

        Assert.Equal(AutomaticAlertDispatchAction.Hold, decision.Action);
        Assert.Equal("invalid_open_episode", decision.ReasonCode);
    }

    private static AlertSourcesStatusResponse Status(params AlertTargetStatus[] targets) =>
        new(CheckedAtUtc, targets);

    private static AlertTargetStatus Target(
        AlertSourceObservationResponse first,
        AlertSourceObservationResponse? second = null,
        string? aggregateLevel = null)
    {
        AlertSourceObservationResponse[] observations = second is null ? [first] : [first, second];
        return new(
            first.Scope,
            first.ScopeId,
            first.ScopeName,
            aggregateLevel ?? first.Level,
            first.AlertLevel,
            observations.Any(item => item.Freshness == "fresh") ? "fresh" : "stale",
            CheckedAtUtc,
            null,
            first,
            observations);
    }

    private static AlertSourceObservationResponse Observation(
        string source,
        string scope,
        string scopeId,
        string level,
        string? alertLevel = null,
        string freshness = "fresh") =>
        new(
            source,
            source,
            scope,
            scopeId,
            ScopeName(scope),
            level,
            alertLevel,
            freshness,
            CheckedAtUtc,
            null,
            null,
            "https://example.test/source",
            null,
            null);

    private static string ScopeName(string scope) => scope switch
    {
        "hromada" => AlertSourceCatalog.KremenchukHromadaName,
        "raion" => AlertSourceCatalog.KremenchukRaionName,
        _ => AlertSourceCatalog.PoltavaOblastName
    };
}
