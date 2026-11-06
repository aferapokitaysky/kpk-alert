using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.AlertSources;
using Watcher.Api.Controllers;

namespace Watcher.UnitTests.AlertSources;

public sealed class AlertsInUaProviderTests
{
    private static readonly DateTimeOffset LastModified = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("R", "active", "red")]
    [InlineData("Y", "active", "yellow")]
    [InlineData("P", "partial", null)]
    [InlineData("N", "unknown", null)]
    [InlineData(" ", "unknown", null)]
    public async Task Read_preserves_v2_level_codes_and_scope_uids(
        string code,
        string expectedState,
        string? expectedAlertLevel)
    {
        ConcurrentBag<HttpRequestMessage> requests = [];
        FakeHttpClientFactory factory = new((_, request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(JsonResponse($"\"{code}\"", LastModified));
        });
        AlertsInUaProvider provider = CreateProvider(factory, "server-only-token");

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, observations.Count);
        Assert.All(observations, observation =>
        {
            Assert.Equal(expectedState, observation.State.ToString().ToLowerInvariant());
            Assert.Equal(expectedAlertLevel, observation.AlertLevel);
            Assert.Equal("fresh", observation.Freshness.ToString().ToLowerInvariant());
            Assert.Equal(LastModified, observation.DataUpdatedAtUtc);
        });
        Assert.Contains(observations, item => item.Scope == AlertScope.Oblast && item.ScopeId == "19");
        Assert.Contains(observations, item => item.Scope == AlertScope.Hromada && item.ScopeId == "1088");
        Assert.All(requests, request =>
        {
            Assert.StartsWith("/v2/iot/active_air_raid_alerts/", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("server-only-token", request.Headers.Authorization?.Parameter);
        });
    }

    [Fact]
    public async Task Read_without_token_does_not_call_provider_and_returns_unknown()
    {
        FakeHttpClientFactory factory = new((_, _, _) =>
            throw new InvalidOperationException("The HTTP handler must not be called."));
        AlertsInUaProvider provider = CreateProvider(factory, null);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, observations.Count);
        Assert.All(observations, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal("not_configured", item.ErrorCode);
            Assert.Equal(AlertFreshness.Unknown, item.Freshness);
        });
    }

    [Fact]
    public async Task Read_uses_last_modified_condition_and_keeps_state_on_304()
    {
        var requestNumber = 0;
        ConcurrentBag<DateTimeOffset?> conditionalDates = [];
        FakeHttpClientFactory factory = new((_, request, _) =>
        {
            conditionalDates.Add(request.Headers.IfModifiedSince);
            var current = Interlocked.Increment(ref requestNumber);
            return Task.FromResult(current <= 2
                ? JsonResponse("\"R\"", LastModified)
                : new HttpResponseMessage(HttpStatusCode.NotModified));
        });
        ManualTimeProvider timeProvider = new(LastModified.AddMinutes(2));
        AlertsInUaProvider provider = CreateProvider(factory, "token", timeProvider);

        IReadOnlyList<AlertSourceObservation> firstRead = await provider.ReadAsync(CancellationToken.None);
        timeProvider.Advance(TimeSpan.FromMinutes(1));
        IReadOnlyList<AlertSourceObservation> secondRead = await provider.ReadAsync(CancellationToken.None);

        Assert.All(firstRead, item => Assert.Equal(AlertState.Active, item.State));
        Assert.All(secondRead, item =>
        {
            Assert.Equal(AlertState.Active, item.State);
            Assert.Equal("red", item.AlertLevel);
            Assert.Equal(AlertFreshness.Fresh, item.Freshness);
            Assert.Equal(LastModified, item.DataUpdatedAtUtc);
        });
        Assert.Equal(2, conditionalDates.Count(value => value == LastModified));
    }

    [Fact]
    public async Task Read_after_429_does_not_turn_last_status_into_inactive()
    {
        var calls = 0;
        FakeHttpClientFactory factory = new((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) <= 2)
            {
                return Task.FromResult(JsonResponse("\"R\"", LastModified));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        AlertsInUaProvider provider = CreateProvider(factory, "token");

        await provider.ReadAsync(CancellationToken.None);
        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal(AlertFreshness.Stale, item.Freshness);
            Assert.Equal("rate_limited", item.ErrorCode);
            Assert.Equal(AlertState.Active, item.LastKnownState);
            Assert.Equal("red", item.LastKnownAlertLevel);
        });
    }

    [Fact]
    public async Task Read_marks_unrecognized_provider_code_unknown()
    {
        FakeHttpClientFactory factory = new((_, _, _) => Task.FromResult(JsonResponse("\"X\"")));
        AlertsInUaProvider provider = CreateProvider(factory, "token");

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal("invalid_response", item.ErrorCode);
        });
    }

    [Fact]
    public async Task Read_rejects_response_larger_than_provider_limit()
    {
        FakeHttpClientFactory factory = new((_, _, _) =>
            Task.FromResult(JsonResponse($"\"{new string('x', 64 * 1024)}\"")));
        AlertsInUaProvider provider = CreateProvider(factory, "token");

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    private static AlertsInUaProvider CreateProvider(
        IHttpClientFactory factory,
        string? token,
        TimeProvider? timeProvider = null) =>
        new(factory, Options.Create(new AlertsInUaOptions(token)), timeProvider ?? TimeProvider.System,
            NullLogger<AlertsInUaProvider>.Instance);

    private static HttpResponseMessage JsonResponse(string json, DateTimeOffset? lastModified = null)
    {
        HttpResponseMessage response = new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (lastModified is DateTimeOffset modified)
        {
            response.Content.Headers.LastModified = modified;
        }
        return response;
    }
}

public sealed class NeptunProviderTests
{
    [Fact]
    public async Task Read_keeps_hromada_unknown_and_reports_matching_raion_separately()
    {
        const string payload = """
            {
              "oblasts": [{ "key": "poltavska", "name": "Полтавська область", "oblast": "", "since": "2026-11-05T10:00:00Z" }],
              "raions": [{ "key": "poltavska:kremenchutskyi", "name": "Кременчуцький район", "oblast": "Полтавська область", "since": "2026-11-05T10:01:00Z" }]
            }
            """;
        FakeHttpClientFactory factory = new((_, _, _) =>
            Task.FromResult(JsonResponse(payload)));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, observations.Count);
        Assert.Contains(observations, item =>
            item.Scope == AlertScope.Oblast &&
            item.ScopeId == AlertSourceCatalog.PoltavaOblastId &&
            item.State == AlertState.Active);
        Assert.Contains(observations, item => item.Scope == AlertScope.Raion && item.State == AlertState.Active);
        Assert.DoesNotContain(observations, item => item.Scope == AlertScope.Hromada);
        Assert.All(observations, item => Assert.Null(item.AlertLevel));
        Assert.All(observations, item => Assert.Null(item.DataUpdatedAtUtc));
    }

    [Fact]
    public async Task Read_uses_stable_keys_when_source_display_names_change()
    {
        const string payload = """
            {
              "oblasts": [{ "key": "poltavska", "name": "Полтавщина", "oblast": "" }],
              "raions": [{ "key": "poltavska:kremenchutskyi", "name": "Kremenchuk District", "oblast": "Poltava Region" }]
            }
            """;
        FakeHttpClientFactory factory = new((_, _, _) => Task.FromResult(JsonResponse(payload)));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal(AlertState.Active, item.State));
        Assert.Contains(observations, item =>
            item.Scope == AlertScope.Oblast && item.ScopeId == AlertSourceCatalog.PoltavaOblastId);
        Assert.Contains(observations, item =>
            item.Scope == AlertScope.Raion && item.ScopeId == AlertSourceCatalog.KremenchukRaionId);
    }

    [Fact]
    public async Task Read_treats_absent_location_in_a_valid_snapshot_as_inactive()
    {
        FakeHttpClientFactory factory = new((_, _, _) =>
            Task.FromResult(JsonResponse("{\"oblasts\":[],\"raions\":[]}")));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal(AlertState.Inactive, item.State));
        Assert.All(observations, item => Assert.Equal(AlertFreshness.Fresh, item.Freshness));
    }

    [Fact]
    public async Task Read_invalid_snapshot_is_unknown_not_inactive()
    {
        FakeHttpClientFactory factory = new((_, _, _) =>
            Task.FromResult(JsonResponse("{\"oblasts\":[]}")));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal(AlertState.Unknown, item.State));
        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    [Fact]
    public async Task Read_rejects_response_larger_than_provider_limit()
    {
        FakeHttpClientFactory factory = new((_, _, _) =>
            Task.FromResult(JsonResponse($"{{\"padding\":\"{new string('x', 64 * 1024)}\",\"oblasts\":[],\"raions\":[]}}")));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    [Fact]
    public async Task Read_rejects_streaming_response_over_limit_without_content_length()
    {
        var payload = $"{{\"padding\":\"{new string('x', 64 * 1024)}\",\"oblasts\":[],\"raions\":[]}}";
        FakeHttpClientFactory factory = new((_, _, _) =>
        {
            StreamContent content = new(new NonSeekableMemoryStream(Encoding.UTF8.GetBytes(payload)));
            Assert.Null(content.Headers.ContentLength);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    [Fact]
    public async Task Read_rejects_too_many_locations()
    {
        const string location = "{\"key\":\"other\",\"name\":\"Other\",\"oblast\":\"\"}";
        var payload = $"{{\"oblasts\":[{string.Join(',', Enumerable.Repeat(location, 257))}],\"raions\":[]}}";
        FakeHttpClientFactory factory = new((_, _, _) => Task.FromResult(JsonResponse(payload)));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    [Fact]
    public async Task Read_duplicate_stable_key_is_unknown_not_inactive()
    {
        const string payload = """
            {"oblasts":[
              {"key":"poltavska","name":"Полтавська область","oblast":""},
              {"key":"poltavska","name":"Полтавська область","oblast":""}
            ],"raions":[]}
            """;
        FakeHttpClientFactory factory = new((_, _, _) => Task.FromResult(JsonResponse(payload)));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal(AlertState.Unknown, item.State));
        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    [Fact]
    public async Task Read_target_name_with_unrecognized_key_is_unknown_not_inactive()
    {
        FakeHttpClientFactory factory = new((_, _, _) => Task.FromResult(JsonResponse(
            """{"oblasts":[{"key":"poltava","name":"Полтавська область","oblast":""}],"raions":[]}""")));
        NeptunProvider provider = CreateProvider(factory);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.All(observations, item => Assert.Equal(AlertState.Unknown, item.State));
        Assert.All(observations, item => Assert.Equal("invalid_response", item.ErrorCode));
    }

    [Fact]
    public async Task Read_rate_limit_keeps_prior_values_only_as_stale_context()
    {
        var calls = 0;
        FakeHttpClientFactory factory = new((_, _, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                return Task.FromResult(JsonResponse("""{"oblasts":[{"key":"poltavska","name":"Полтавська область","oblast":""}],"raions":[]}"""));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        });
        NeptunProvider provider = CreateProvider(factory);

        await provider.ReadAsync(CancellationToken.None);
        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        AlertSourceObservation oblast = Assert.Single(observations, item => item.Scope == AlertScope.Oblast);
        Assert.Equal(AlertState.Unknown, oblast.State);
        Assert.Equal(AlertState.Active, oblast.LastKnownState);
        Assert.Equal(AlertFreshness.Stale, oblast.Freshness);
        Assert.Equal("rate_limited", oblast.ErrorCode);
    }

    private static NeptunProvider CreateProvider(IHttpClientFactory factory, TimeProvider? timeProvider = null) =>
        new(factory, timeProvider ?? TimeProvider.System, NullLogger<NeptunProvider>.Instance);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }
}

public sealed class AlertStatusAggregatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly AlertStatusAggregator _aggregator = new();

    [Fact]
    public void Aggregate_returns_unknown_targets_when_no_provider_observations_exist()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now, []);

        Assert.All(result.Targets, target =>
        {
            Assert.Equal("unknown", target.Level);
            Assert.Equal("unknown", target.Freshness);
            Assert.Null(target.CheckedAtUtc);
            Assert.Null(target.AuthoritativeObservation);
            Assert.Empty(target.Observations);
        });
    }

    [Fact]
    public void Aggregate_does_not_use_neptun_raion_as_hromada_status()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Raion, AlertSourceCatalog.KremenchukRaionId, "neptun", AlertState.Active)
        ]);

        AlertTargetStatus hromada = Assert.Single(result.Targets, item => item.Scope == "hromada");
        AlertTargetStatus raion = Assert.Single(result.Targets, item => item.Scope == "raion");
        Assert.Equal("unknown", hromada.Level);
        Assert.Equal("active", raion.Level);
    }

    [Fact]
    public void Aggregate_keeps_partial_active_and_inactive_as_distinct_states()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Partial),
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Inactive)
        ]);

        Assert.Equal("partial", Assert.Single(result.Targets, item => item.Scope == "hromada").Level);
        Assert.Equal("inactive", Assert.Single(result.Targets, item => item.Scope == "oblast").Level);
    }

    [Fact]
    public void Aggregate_marks_same_scope_disagreement_as_conflict()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Active),
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.NeptunCode, AlertState.Inactive)
        ]);

        Assert.Equal("conflict", Assert.Single(result.Targets, item => item.Scope == "oblast").Level);
    }

    [Fact]
    public void Aggregate_prefers_neptun_over_alerts_in_ua_when_both_are_fresh()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Active) with { AlertLevel = "red" },
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.NeptunCode, AlertState.Inactive)
        ]);

        AlertTargetStatus oblast = Assert.Single(result.Targets, item => item.Scope == "oblast");
        Assert.Equal("conflict", oblast.Level);
        Assert.Equal("inactive", oblast.AuthoritativeObservation?.Level);
        Assert.Equal(AlertSourceCatalog.NeptunCode, oblast.AuthoritativeObservation?.Source);
    }

    [Fact]
    public void Aggregate_prefers_ukrainealarm_over_neptun_and_alerts_in_ua()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Inactive),
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.NeptunCode, AlertState.Inactive),
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.UkraineAlarmCode, AlertState.Active)
        ]);

        AlertTargetStatus oblast = Assert.Single(result.Targets, item => item.Scope == "oblast");
        Assert.Equal("conflict", oblast.Level);
        Assert.Equal("active", oblast.AuthoritativeObservation?.Level);
        Assert.Equal(AlertSourceCatalog.UkraineAlarmCode, oblast.AuthoritativeObservation?.Source);
    }

    [Fact]
    public void Aggregate_uses_neptun_only_when_primary_status_is_not_fresh()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Unknown) with
            {
                Freshness = AlertFreshness.Stale,
                LastKnownState = AlertState.Inactive
            },
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.NeptunCode, AlertState.Active)
        ]);

        AlertTargetStatus oblast = Assert.Single(result.Targets, item => item.Scope == "oblast");
        Assert.Equal("active", oblast.Level);
        Assert.Equal(AlertSourceCatalog.NeptunCode, oblast.AuthoritativeObservation?.Source);
        Assert.Null(oblast.AlertLevel);
    }

    [Fact]
    public void Aggregate_does_not_use_stale_values_as_current_status()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Unknown) with
            {
                Freshness = AlertFreshness.Stale,
                LastKnownState = AlertState.Inactive
            }
        ]);

        AlertTargetStatus hromada = Assert.Single(result.Targets, item => item.Scope == "hromada");
        Assert.Equal("unknown", hromada.Level);
        Assert.Equal("stale", hromada.Freshness);
        Assert.Equal("inactive", Assert.Single(hromada.Observations).LastKnownLevel);
    }

    [Fact]
    public void Aggregate_detects_conflicting_red_and_yellow_severity_without_selecting_one()
    {
        AlertSourcesStatusResponse result = _aggregator.Aggregate(Now,
        [
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.AlertsInUaCode, AlertState.Active) with { AlertLevel = "red" },
            Observation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                "other", AlertState.Active) with { AlertLevel = "yellow" }
        ]);

        AlertTargetStatus oblast = Assert.Single(result.Targets, item => item.Scope == "oblast");
        Assert.Equal("conflict", oblast.Level);
        Assert.Null(oblast.AlertLevel);
    }

    private static AlertSourceObservation Observation(
        AlertScope scope,
        string scopeId,
        string source,
        AlertState state) =>
        new(source, source, scope, scopeId, scope.ToString(), state, null, AlertFreshness.Fresh,
            Now, Now, null, "https://example.test/");
}

public sealed class AlertSourcesStatusServiceTests
{
    [Fact]
    public async Task GetStatus_coalesces_concurrent_requests_and_reuses_one_minute_cache()
    {
        ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        CountingAlertSourceProvider first = new("one");
        CountingAlertSourceProvider second = new("two");
        AlertSourcesStatusService service = new(
            [first, second],
            new AlertStatusAggregator(),
            timeProvider,
            NullLogger<AlertSourcesStatusService>.Instance);

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => service.GetStatusAsync(CancellationToken.None)));
        Assert.Equal(1, first.CallCount);
        Assert.Equal(1, second.CallCount);

        timeProvider.Advance(TimeSpan.FromSeconds(59));
        await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(1, first.CallCount);

        timeProvider.Advance(TimeSpan.FromSeconds(1));
        await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(2, first.CallCount);
        Assert.Equal(2, second.CallCount);
    }

    [Fact]
    public async Task GetStatus_keeps_healthy_provider_when_another_provider_throws()
    {
        ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        CountingAlertSourceProvider healthy = new("healthy");
        ThrowingAlertSourceProvider failing = new();
        AlertSourcesStatusService service = new(
            [healthy, failing],
            new AlertStatusAggregator(),
            timeProvider,
            NullLogger<AlertSourcesStatusService>.Instance);

        AlertSourcesStatusResponse response = await service.GetStatusAsync(CancellationToken.None);

        AlertTargetStatus oblast = Assert.Single(response.Targets, item => item.Scope == "oblast");
        AlertTargetStatus hromada = Assert.Single(response.Targets, item => item.Scope == "hromada");
        Assert.Equal("active", oblast.Level);
        Assert.Equal("unknown", hromada.Level);
        Assert.Equal("source_unavailable", Assert.Single(hromada.Observations).ErrorCode);
        Assert.Equal("failing", Assert.Single(hromada.Observations).Source);
    }

    [Fact]
    public async Task GetStatus_propagates_request_cancellation_without_fallback()
    {
        ManualTimeProvider timeProvider = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        CancellableAlertSourceProvider provider = new();
        AlertSourcesStatusService service = new(
            [provider],
            new AlertStatusAggregator(),
            timeProvider,
            NullLogger<AlertSourcesStatusService>.Instance);
        using CancellationTokenSource cancellation = new();

        Task<AlertSourcesStatusResponse> pending = service.GetStatusAsync(cancellation.Token);
        await provider.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(provider.FallbackCalled);
    }

    private sealed class CountingAlertSourceProvider(string source) : IAlertSourceProvider
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _callCount);
            await Task.Delay(10, cancellationToken);
            return [new(source, source, AlertScope.Oblast, "19", "Полтавська область", AlertState.Active,
                null, AlertFreshness.Fresh, DateTimeOffset.UtcNow, null, null, "https://example.test/")];
        }

        public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
            DateTimeOffset checkedAtUtc,
            string errorCode) =>
            [new(source, source, AlertScope.Oblast, "19", "Полтавська область", AlertState.Unknown,
                null, AlertFreshness.Unknown, checkedAtUtc, null, errorCode, "https://example.test/")];
    }

    private sealed class ThrowingAlertSourceProvider : IAlertSourceProvider
    {
        public Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Unexpected provider failure");

        public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
            DateTimeOffset checkedAtUtc,
            string errorCode) =>
            [new("failing", "Failing", AlertScope.Hromada, "1088", "Кременчуцька громада",
                AlertState.Unknown, null, AlertFreshness.Unknown, checkedAtUtc, null, errorCode,
                "https://example.test/")];
    }

    private sealed class CancellableAlertSourceProvider : IAlertSourceProvider
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FallbackCalled { get; private set; }

        public async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken)
        {
            ReadStarted.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [];
        }

        public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
            DateTimeOffset checkedAtUtc,
            string errorCode)
        {
            FallbackCalled = true;
            return [];
        }
    }
}

internal sealed class FakeHttpClientFactory(
    Func<string, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responseFactory) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(new DelegateHttpMessageHandler((request, token) =>
        responseFactory(name, request, token)), disposeHandler: true)
    {
        BaseAddress = new Uri(name == AlertsInUaProvider.ClientName
            ? AlertSourceCatalog.AlertsInUaBaseUrl
            : AlertSourceCatalog.NeptunBaseUrl)
    };
}

internal sealed class DelegateHttpMessageHandler(
    Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken) => send(request, cancellationToken);
}

internal sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
{
    private DateTimeOffset _current = current;

    public override DateTimeOffset GetUtcNow() => _current;

    public void Advance(TimeSpan duration) => _current += duration;
}
