using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class UkraineAlarmProviderTests
{
    private const string Regions = """
        {"states":[
          {"regionId":"state-from-api","regionName":"Полтавська область","regionType":"State","regionChildIds":[
            {"regionId":"community-from-api","regionName":"Кременчуцька громада","regionType":"Community","regionChildIds":[]}
          ]}
        ]}
        """;

    [Fact]
    public async Task Resolves_ids_from_region_catalog_and_reads_each_target_status()
    {
        List<string> paths = [];
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path switch
            {
                "/api/v3/alerts/status" => JsonResponse("""{"lastActionIndex":7}"""),
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" => JsonResponse(
                    """[{"regionId":"state-from-api","regionType":"State","regionName":"Полтавська область","activeAlerts":[{"regionId":"state-from-api","regionType":"State","type":"Air"}]}]"""),
                "/api/v3/alerts/community-from-api" => JsonResponse(
                    """[{"regionId":"community-from-api","regionType":"Community","regionName":"Кременчуцька громада","activeAlerts":[]}]"""),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        }, clock);

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, observations.Count);
        Assert.Contains(observations, item => item.Scope == AlertScope.Oblast &&
            item.ScopeId == AlertSourceCatalog.PoltavaOblastId && item.State == AlertState.Active);
        Assert.Contains(observations, item => item.Scope == AlertScope.Hromada &&
            item.ScopeId == AlertSourceCatalog.KremenchukHromadaId && item.State == AlertState.Inactive);
        Assert.Contains("/api/v3/alerts/state-from-api", paths);
        Assert.Contains("/api/v3/alerts/community-from-api", paths);
        Assert.DoesNotContain("/api/v3/alerts/19", paths);
        Assert.DoesNotContain("/api/v3/alerts/1088", paths);
    }

    [Fact]
    public async Task Reuses_snapshot_when_action_index_is_unchanged()
    {
        var regionRequests = 0;
        var statusChecks = 0;
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        DateTimeOffset initialCheckedAt = clock.GetUtcNow();
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v3/alerts/status")
            {
                statusChecks++;
                return Task.FromResult(JsonResponse("""{"lastActionIndex":7}"""));
            }

            regionRequests++;
            return Task.FromResult(path switch
            {
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" or "/api/v3/alerts/community-from-api" =>
                    JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        }, clock);

        await provider.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(10));
        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(2, statusChecks);
        Assert.Equal(3, regionRequests);
        Assert.All(observations, item => Assert.Equal(AlertFreshness.Fresh, item.Freshness));
        Assert.All(observations, item => Assert.Equal(initialCheckedAt, item.CheckedAtUtc));
    }

    [Fact]
    public async Task Concurrent_reads_are_serialized_around_snapshot_state()
    {
        var statusChecks = 0;
        var regionRequests = 0;
        TaskCompletionSource firstStatusRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstStatusRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        UkraineAlarmProvider provider = CreateProvider(async (request, cancellationToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v3/alerts/status")
            {
                if (Interlocked.Increment(ref statusChecks) == 1)
                {
                    firstStatusRequest.TrySetResult();
                    await releaseFirstStatusRequest.Task.WaitAsync(cancellationToken);
                }

                return JsonResponse("""{"lastActionIndex":7}""");
            }

            Interlocked.Increment(ref regionRequests);
            return path switch
            {
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" or "/api/v3/alerts/community-from-api" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            };
        }, clock);

        Task<IReadOnlyList<AlertSourceObservation>> firstRead = provider.ReadAsync(CancellationToken.None);
        await firstStatusRequest.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task<IReadOnlyList<AlertSourceObservation>> secondRead = provider.ReadAsync(CancellationToken.None);
        await Task.Yield();

        Assert.Equal(1, statusChecks);
        releaseFirstStatusRequest.TrySetResult();
        await Task.WhenAll(firstRead, secondRead);

        Assert.Equal(2, statusChecks);
        Assert.Equal(3, regionRequests);
    }

    [Fact]
    public async Task Callback_without_new_action_index_marks_cached_snapshot_stale()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var signal = new UkraineAlarmRefreshSignal();
        var requestCount = 0;
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            requestCount++;
            var path = request.RequestUri!.AbsolutePath;
            return Task.FromResult(path switch
            {
                "/api/v3/alerts/status" => JsonResponse("""{"lastActionIndex":7}"""),
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" or "/api/v3/alerts/community-from-api" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        }, clock, signal);

        await provider.ReadAsync(CancellationToken.None);
        Assert.True(signal.TrySignal(clock.GetUtcNow()));
        IReadOnlyList<AlertSourceObservation> stale = await provider.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(2));
        IReadOnlyList<AlertSourceObservation> stillStale = await provider.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(58));
        IReadOnlyList<AlertSourceObservation> confirmedStale = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(6, requestCount);
        Assert.All(stale.Concat(stillStale), item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal(AlertFreshness.Stale, item.Freshness);
            Assert.Equal(AlertState.Inactive, item.LastKnownState);
            Assert.Equal("retry_backoff", item.ErrorCode);
        });
        Assert.All(confirmedStale, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal(AlertFreshness.Stale, item.Freshness);
            Assert.Equal(AlertState.Inactive, item.LastKnownState);
            Assert.Equal("action_index_not_advanced", item.ErrorCode);
        });

        Assert.False(signal.Consume());
    }

    [Fact]
    public async Task Action_index_rollback_preserves_only_stale_context()
    {
        var actionIndexCalls = 0;
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v3/alerts/status")
            {
                var call = Interlocked.Increment(ref actionIndexCalls);
                var index = call < 3 ? "9" : "8";
                return Task.FromResult(JsonResponse($"{{\"lastActionIndex\":{index}}}"));
            }

            return Task.FromResult(path switch
            {
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" => JsonResponse(
                    """[{"regionId":"state-from-api","regionType":"State","regionName":"Полтавська область","activeAlerts":[{"regionId":"state-from-api","regionType":"State","type":"Air"}]}]"""),
                "/api/v3/alerts/community-from-api" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        }, clock);

        await provider.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        IReadOnlyList<AlertSourceObservation> stale = await provider.ReadAsync(CancellationToken.None);

        Assert.All(stale, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal(AlertFreshness.Stale, item.Freshness);
            Assert.Equal("action_index_rollback", item.ErrorCode);
        });
        Assert.Equal(AlertState.Active, Assert.Single(stale, item => item.Scope == AlertScope.Oblast).LastKnownState);
    }

    [Fact]
    public async Task Discards_snapshot_if_action_index_changes_while_regions_are_read()
    {
        var statusCalls = 0;
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v3/alerts/status")
            {
                var call = Interlocked.Increment(ref statusCalls);
                var index = call == 1 ? "5" : "6";
                return Task.FromResult(JsonResponse($"{{\"lastActionIndex\":{index}}}"));
            }

            return Task.FromResult(path switch
            {
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" or "/api/v3/alerts/community-from-api" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        }, clock);

        IReadOnlyList<AlertSourceObservation> inconsistent = await provider.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        IReadOnlyList<AlertSourceObservation> consistent = await provider.ReadAsync(CancellationToken.None);

        Assert.All(inconsistent, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal("snapshot_changed_during_read", item.ErrorCode);
        });
        Assert.All(consistent, item =>
        {
            Assert.Equal(AlertState.Inactive, item.State);
            Assert.Equal(AlertFreshness.Fresh, item.Freshness);
            Assert.Null(item.ErrorCode);
        });
        Assert.Equal(4, statusCalls);
    }

    [Fact]
    public async Task Rate_limit_enters_backoff_without_turning_previous_alert_inactive()
    {
        var statusCalls = 0;
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v3/alerts/status")
            {
                var call = Interlocked.Increment(ref statusCalls);
                if (call == 3)
                {
                    HttpResponseMessage limited = new(HttpStatusCode.TooManyRequests);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                        TimeSpan.FromMinutes(3));
                    return Task.FromResult(limited);
                }

                return Task.FromResult(JsonResponse("""{"lastActionIndex":3}"""));
            }

            return Task.FromResult(path switch
            {
                "/api/v3/regions" => JsonResponse(Regions),
                "/api/v3/alerts/state-from-api" => JsonResponse(
                    """[{"regionId":"state-from-api","regionType":"State","regionName":"Полтавська область","activeAlerts":[{"regionId":"state-from-api","regionType":"State","type":"Air"}]}]"""),
                "/api/v3/alerts/community-from-api" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        }, clock);

        await provider.ReadAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        IReadOnlyList<AlertSourceObservation> limited = await provider.ReadAsync(CancellationToken.None);
        IReadOnlyList<AlertSourceObservation> duringBackoff = await provider.ReadAsync(CancellationToken.None);
        Assert.Equal(3, statusCalls);
        Assert.All(duringBackoff, item =>
        {
            Assert.Equal(AlertState.Unknown, item.State);
            Assert.Equal(AlertFreshness.Stale, item.Freshness);
            Assert.Equal("rate_limited", item.ErrorCode);
        });
        Assert.Equal(AlertState.Active,
            Assert.Single(duringBackoff, item => item.Scope == AlertScope.Oblast).LastKnownState);
        clock.Advance(TimeSpan.FromMinutes(2));
        await provider.ReadAsync(CancellationToken.None);
        Assert.Equal(3, statusCalls);
        clock.Advance(TimeSpan.FromMinutes(1));
        IReadOnlyList<AlertSourceObservation> retry = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal("rate_limited", limited[0].ErrorCode);
        Assert.Equal(AlertState.Active, limited[0].LastKnownState);
        Assert.All(retry, item => Assert.Equal(AlertFreshness.Fresh, item.Freshness));
        Assert.Equal(4, statusCalls);
    }

    [Fact]
    public async Task Missing_unique_region_match_stays_unknown_without_requesting_a_guessed_id()
    {
        List<string> paths = [];
        UkraineAlarmProvider provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            paths.Add(path);
            return Task.FromResult(path switch
            {
                "/api/v3/alerts/status" => JsonResponse("""{"lastActionIndex":11}"""),
                "/api/v3/regions" => JsonResponse(
                    """{"states":[{"regionId":"state-from-api","regionName":"Полтавська область","regionType":"State","regionChildIds":[] }]}"""),
                "/api/v3/alerts/state-from-api" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        });

        IReadOnlyList<AlertSourceObservation> observations = await provider.ReadAsync(CancellationToken.None);

        AlertSourceObservation hromada = Assert.Single(observations, item => item.Scope == AlertScope.Hromada);
        Assert.Equal(AlertState.Unknown, hromada.State);
        Assert.Equal("region_not_found", hromada.ErrorCode);
        Assert.DoesNotContain("/api/v3/alerts/1088", paths);
    }

    private static UkraineAlarmProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send,
        TimeProvider? timeProvider = null,
        IUkraineAlarmRefreshSignal? signal = null) =>
        new(
            CreateClient(send),
            Options.Create(new UkraineAlarmOptions { Token = "test-token" }),
            signal ?? new UkraineAlarmRefreshSignal(),
            timeProvider ?? TimeProvider.System,
            NullLogger<UkraineAlarmProvider>.Instance);

    private static UkraineAlarmApiClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        HttpClient httpClient = new(new TestHandler(send));
        return new UkraineAlarmApiClient(httpClient,
            Options.Create(new UkraineAlarmOptions { Token = "test-token" }));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
