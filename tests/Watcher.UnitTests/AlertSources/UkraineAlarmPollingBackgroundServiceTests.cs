using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class UkraineAlarmPollingBackgroundServiceTests
{
    [Fact]
    public async Task Callback_wakes_worker_but_upstream_poll_still_observes_minimum_interval()
    {
        ManualTimeProvider clock = new(new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero));
        var statusCalls = 0;
        TaskCompletionSource initialPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource callbackPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UkraineAlarmApiClient client = CreateClient((request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/v3/alerts/status")
            {
                var calls = Interlocked.Increment(ref statusCalls);
                if (calls >= 2)
                {
                    initialPoll.TrySetResult();
                }

                if (calls >= 3)
                {
                    callbackPoll.TrySetResult();
                }

                return Task.FromResult(JsonResponse("""{"lastActionIndex":1}"""));
            }

            return Task.FromResult(request.RequestUri.AbsolutePath switch
            {
                "/api/v3/regions" => JsonResponse(
                    """{"states":[{"regionId":"state","regionName":"Полтавська область","regionType":"State","regionChildIds":[{"regionId":"community","regionName":"Кременчуцька громада","regionType":"Community","regionChildIds":[]}]}]}"""),
                "/api/v3/alerts/state" or "/api/v3/alerts/community" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        });
        UkraineAlarmRefreshSignal signal = new();
        UkraineAlarmProvider provider = new(
            client,
            Options.Create(new UkraineAlarmOptions { Token = "test-token" }),
            signal,
            clock,
            NullLogger<UkraineAlarmProvider>.Instance);
        UkraineAlarmPollingBackgroundService service = new(
            provider,
            signal,
            Options.Create(new UkraineAlarmOptions { PollInterval = TimeSpan.FromMinutes(5) }),
            clock,
            NullLogger<UkraineAlarmPollingBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await initialPoll.Task.WaitAsync(TimeSpan.FromSeconds(1));
            await Task.Delay(20);
            Assert.True(signal.TrySignal(clock.GetUtcNow()));
            clock.Advance(TimeSpan.FromSeconds(59));
            await Task.Delay(20);
            Assert.Equal(2, statusCalls);

            clock.Advance(TimeSpan.FromSeconds(1));
            await callbackPoll.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Performs_initial_poll_independently_of_status_endpoint_cache()
    {
        var statusCalls = 0;
        TaskCompletionSource initialPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        UkraineAlarmApiClient client = CreateClient((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/v3/alerts/status")
            {
                if (Interlocked.Increment(ref statusCalls) >= 2)
                {
                    initialPoll.TrySetResult();
                }

                return Task.FromResult(JsonResponse("""{"lastActionIndex":1}"""));
            }

            return Task.FromResult(path switch
            {
                "/api/v3/regions" => JsonResponse(
                    """{"states":[{"regionId":"state","regionName":"Полтавська область","regionType":"State","regionChildIds":[{"regionId":"community","regionName":"Кременчуцька громада","regionType":"Community","regionChildIds":[]}]}]}"""),
                "/api/v3/alerts/state" or "/api/v3/alerts/community" => JsonResponse("[]"),
                _ => throw new InvalidOperationException("Unexpected provider request.")
            });
        });
        UkraineAlarmProvider provider = new(
            client,
            Options.Create(new UkraineAlarmOptions { Token = "test-token" }),
            new UkraineAlarmRefreshSignal(),
            TimeProvider.System,
            NullLogger<UkraineAlarmProvider>.Instance);
        UkraineAlarmPollingBackgroundService service = new(
            provider,
            new UkraineAlarmRefreshSignal(),
            Options.Create(new UkraineAlarmOptions { PollInterval = TimeSpan.FromMinutes(1) }),
            TimeProvider.System,
            NullLogger<UkraineAlarmPollingBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await initialPoll.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(statusCalls >= 2);
    }

    [Fact]
    public void Rejects_polling_interval_below_one_minute()
    {
        UkraineAlarmProvider provider = new(
            CreateClient((_, _) => Task.FromResult(JsonResponse("{}"))),
            Options.Create(new UkraineAlarmOptions { Token = "test-token" }),
            new UkraineAlarmRefreshSignal(),
            TimeProvider.System,
            NullLogger<UkraineAlarmProvider>.Instance);

        Assert.Throws<InvalidOperationException>(() => new UkraineAlarmPollingBackgroundService(
            provider,
            new UkraineAlarmRefreshSignal(),
            Options.Create(new UkraineAlarmOptions { PollInterval = TimeSpan.FromSeconds(59) }),
            TimeProvider.System,
            NullLogger<UkraineAlarmPollingBackgroundService>.Instance));
    }

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

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class ManualTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = initialTime;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _now;
            }
        }

        public override long GetTimestamp() => GetUtcNow().UtcTicks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            lock (_sync)
            {
                ManualTimer timer = new(this, callback, state);
                timer.Change(dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }

        public void Advance(TimeSpan duration)
        {
            List<ManualTimer> dueTimers;
            lock (_sync)
            {
                _now += duration;
                dueTimers = _timers.Where(timer => timer.IsDue(_now)).ToList();
                foreach (ManualTimer timer in dueTimers)
                {
                    timer.Disable();
                }
            }

            foreach (ManualTimer timer in dueTimers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
        {
            private DateTimeOffset? _dueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    _dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + dueTime;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._sync)
                {
                    _dueAt = null;
                    owner._timers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool IsDue(DateTimeOffset now) => _dueAt is DateTimeOffset dueAt && dueAt <= now;

            public void Disable() => _dueAt = null;

            public void Fire() => callback(state);
        }
    }
}
