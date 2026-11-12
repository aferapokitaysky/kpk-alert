using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class NeptunRealtimeMonitorTests
{
    [Fact]
    public async Task OversizedRestResponsePreservesLastKnownStateAsStale()
    {
        var requestCount = 0;
        FakeHttpClientFactory httpFactory = new((_, _, _) =>
        {
            if (Interlocked.Increment(ref requestCount) == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"oblasts":[{"key":"poltavska","name":"Полтавська область","oblast":""}],"raions":[]}
                        """, Encoding.UTF8, "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new OversizedStream(NeptunProvider.MaximumRestResponseBytes + 1))
            });
        });
        NeptunProvider provider = new(httpFactory, TimeProvider.System, NullLogger<NeptunProvider>.Instance);

        IReadOnlyList<AlertSourceObservation> initial = await provider.ReadAsync(CancellationToken.None);
        IReadOnlyList<AlertSourceObservation> afterOversizedResponse = await provider.ReadAsync(CancellationToken.None);

        Assert.Equal(AlertState.Active, initial.Single(item => item.Scope == AlertScope.Oblast).State);
        AlertSourceObservation oblast = afterOversizedResponse.Single(item => item.Scope == AlertScope.Oblast);
        Assert.Equal(AlertState.Unknown, oblast.State);
        Assert.Equal(AlertFreshness.Stale, oblast.Freshness);
        Assert.Equal(AlertState.Active, oblast.LastKnownState);
        Assert.Equal("invalid_response", oblast.ErrorCode);
    }

    [Fact]
    public async Task AlertsReplaceStatusSnapshotDoesNotAndReconnectUsesRestFallback()
    {
        var restRequests = 0;
        FakeHttpClientFactory httpFactory = new((_, _, _) =>
        {
            Interlocked.Increment(ref restRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"oblasts":[],"raions":[]}
                    """, Encoding.UTF8, "application/json")
            });
        });
        NeptunProvider provider = new(
            httpFactory,
            TimeProvider.System,
            NullLogger<NeptunProvider>.Instance);
        FakeConnectionFactory connectionFactory = new(holdAlertFrame: true);
        NeptunRealtimeOptions options = new()
        {
            HeartbeatTimeout = TimeSpan.FromSeconds(5),
            RestFallbackInterval = TimeSpan.FromSeconds(15),
            InitialReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaximumReconnectDelay = TimeSpan.FromMilliseconds(20)
        };
        (NeptunRealtimeMonitor monitor, AlertSourceChangeSignal changeSignal, ServiceProvider serviceProvider,
            AlertSourcesStatusService statusService) =
            CreateMonitor(provider, connectionFactory, options);

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => connectionFactory.FirstConnection is not null);
            using (CancellationTokenSource startupSignalTimeout = new(TimeSpan.FromSeconds(3)))
            {
                await changeSignal.WaitForChangeAsync(startupSignalTimeout.Token);
            }

            AlertSourcesStatusResponse cachedStatus = await statusService.GetStatusAsync(CancellationToken.None);
            AlertTargetStatus cachedOblast = cachedStatus.Targets.Single(item => item.Scope == "oblast");
            Assert.Equal("inactive", cachedOblast.AuthoritativeObservation?.Level);
            connectionFactory.ReleaseAlertFrame();
            await WaitForAsync(() => connectionFactory.FirstConnection?.AlertApplied.Task.IsCompleted == true);
            await WaitForAsync(async () =>
            {
                IReadOnlyList<AlertSourceObservation> current = await provider.ReadAsync(CancellationToken.None);
                return current.All(item => item.State == AlertState.Active);
            });
            using (CancellationTokenSource alertChangeTimeout = new(TimeSpan.FromSeconds(3)))
            {
                await changeSignal.WaitForChangeAsync(alertChangeTimeout.Token);
            }

            AlertSourcesStatusResponse updatedStatus = await statusService.GetStatusAsync(CancellationToken.None);
            AlertTargetStatus updatedOblast = updatedStatus.Targets.Single(item => item.Scope == "oblast");
            Assert.Equal("active", updatedOblast.AuthoritativeObservation?.Level);
            await WaitForAsync(async () =>
            {
                IReadOnlyList<AlertSourceObservation> current = await provider.ReadAsync(CancellationToken.None);
                return current.Count == 2 && current.All(item => item.State == AlertState.Active);
            });
            IReadOnlyList<AlertSourceObservation> whileConnected = await provider.ReadAsync(CancellationToken.None);
            Assert.Equal(AlertState.Active, whileConnected.Single(item => item.Scope == AlertScope.Oblast).State);
            Assert.Equal(AlertState.Active, whileConnected.Single(item => item.Scope == AlertScope.Raion).State);

            await WaitForAsync(async () =>
            {
                IReadOnlyList<AlertSourceObservation> current = await provider.ReadAsync(CancellationToken.None);
                return Volatile.Read(ref restRequests) >= 2 && connectionFactory.ConnectionCount >= 2 &&
                       current.Count == 2 && current.All(item =>
                           item.State == AlertState.Inactive && item.Freshness == AlertFreshness.Fresh);
            }, TimeSpan.FromSeconds(20));
            IReadOnlyList<AlertSourceObservation> afterFallback = await provider.ReadAsync(CancellationToken.None);
            Assert.All(afterFallback, observation => Assert.Equal(AlertState.Inactive, observation.State));
            Assert.Equal(2, Volatile.Read(ref restRequests));
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            await serviceProvider.DisposeAsync();
        }
    }

    [Fact]
    public async Task MissingHeartbeatMarksLastKnownSnapshotStale()
    {
        FakeHttpClientFactory httpFactory = new((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"oblasts":[{"key":"poltavska","name":"Полтавська область","oblast":""}],"raions":[]}
                """, Encoding.UTF8, "application/json")
        }));
        NeptunProvider provider = new(httpFactory, TimeProvider.System, NullLogger<NeptunProvider>.Instance);
        FakeConnectionFactory connectionFactory = new(blockAfterAlerts: true);
        (NeptunRealtimeMonitor monitor, _, ServiceProvider serviceProvider, _) = CreateMonitor(
            provider,
            connectionFactory,
            new NeptunRealtimeOptions
            {
                HeartbeatTimeout = TimeSpan.FromMilliseconds(100),
                RestFallbackInterval = TimeSpan.FromSeconds(15),
                InitialReconnectDelay = TimeSpan.FromSeconds(5),
                MaximumReconnectDelay = TimeSpan.FromSeconds(5)
            });

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(() => connectionFactory.FirstConnection?.AlertApplied.Task.IsCompleted == true);
            await WaitForAsync(async () =>
            {
                IReadOnlyList<AlertSourceObservation> current = await provider.ReadAsync(CancellationToken.None);
                return current.All(item => item.Freshness == AlertFreshness.Stale &&
                    item.LastKnownState == AlertState.Active && item.ErrorCode == "heartbeat_timeout");
            });
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            await serviceProvider.DisposeAsync();
        }
    }

    [Fact]
    public async Task MalformedRealtimeFrameClearsSnapshotInsteadOfReportingInactive()
    {
        FakeHttpClientFactory httpFactory = new((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {"oblasts":[{"key":"poltavska","name":"Полтавська область","oblast":""}],"raions":[]}
                """, Encoding.UTF8, "application/json")
        }));
        NeptunProvider provider = new(httpFactory, TimeProvider.System, NullLogger<NeptunProvider>.Instance);
        FakeConnectionFactory connectionFactory = new(sendMalformedFrame: true);
        (NeptunRealtimeMonitor monitor, _, ServiceProvider serviceProvider, _) = CreateMonitor(
            provider,
            connectionFactory,
            new NeptunRealtimeOptions
            {
                HeartbeatTimeout = TimeSpan.FromSeconds(5),
                RestFallbackInterval = TimeSpan.FromSeconds(15),
                InitialReconnectDelay = TimeSpan.FromSeconds(5),
                MaximumReconnectDelay = TimeSpan.FromSeconds(5)
            });

        await monitor.StartAsync(CancellationToken.None);
        try
        {
            await WaitForAsync(async () =>
            {
                IReadOnlyList<AlertSourceObservation> current = await provider.ReadAsync(CancellationToken.None);
                return current.All(item => item.State == AlertState.Unknown &&
                    item.Freshness == AlertFreshness.Unknown && item.ErrorCode == "invalid_response");
            });
        }
        finally
        {
            await monitor.StopAsync(CancellationToken.None);
            await serviceProvider.DisposeAsync();
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cancellation = new(timeout ?? TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cancellation = new(timeout ?? TimeSpan.FromSeconds(5));
        while (!await condition())
        {
            await Task.Delay(10, cancellation.Token);
        }
    }

    private static (NeptunRealtimeMonitor Monitor, AlertSourceChangeSignal ChangeSignal, ServiceProvider Services,
        AlertSourcesStatusService StatusService)
        CreateMonitor(
            NeptunProvider provider,
            INeptunRealtimeConnectionFactory connectionFactory,
            NeptunRealtimeOptions options)
    {
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        var statusService = new AlertSourcesStatusService(
            [provider],
            new AlertStatusAggregator(),
            services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<AlertSourcesStatusService>.Instance);
        var changeSignal = new AlertSourceChangeSignal();
        var monitor = new NeptunRealtimeMonitor(
            provider,
            statusService,
            changeSignal,
            connectionFactory,
            options,
            TimeProvider.System,
            NullLogger<NeptunRealtimeMonitor>.Instance);
        return (monitor, changeSignal, services, statusService);
    }

    private sealed class FakeConnectionFactory : INeptunRealtimeConnectionFactory
    {
        private int _connectionCount;
        private readonly TaskCompletionSource _alertFrameGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeConnectionFactory(
            bool blockAfterAlerts = false,
            bool sendMalformedFrame = false,
            bool holdAlertFrame = false)
        {
            this.blockAfterAlerts = blockAfterAlerts;
            this.sendMalformedFrame = sendMalformedFrame;
            if (!holdAlertFrame)
            {
                _alertFrameGate.TrySetResult();
            }
        }

        private readonly bool blockAfterAlerts;
        private readonly bool sendMalformedFrame;

        public int ConnectionCount => Volatile.Read(ref _connectionCount);
        public FakeConnection? FirstConnection { get; private set; }

        public void ReleaseAlertFrame() => _alertFrameGate.TrySetResult();

        public Task<INeptunRealtimeConnection> ConnectAsync(Uri streamUri, CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _connectionCount);
            FakeConnection connection = new(
                number == 1 && !blockAfterAlerts,
                number == 1,
                number == 1 && sendMalformedFrame,
                _alertFrameGate.Task);
            FirstConnection ??= connection;
            return Task.FromResult<INeptunRealtimeConnection>(connection);
        }
    }

    private sealed class FakeConnection(
        bool closeAfterDelay,
        bool sendAlertFrame,
        bool sendMalformedFrame,
        Task alertFrameGate) : INeptunRealtimeConnection
    {
        private int _received;
        public TaskCompletionSource AlertApplied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<NeptunRealtimeMessage> ReceiveAsync(
            int maximumMessageBytes,
            CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _received) == 1)
            {
                if (sendMalformedFrame)
                {
                    return new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("{malformed"));
                }

                return new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""
                    {"type":"snapshot","data":{"threats":[]}}
                    """));
            }

            if (_received == 2)
            {
                if (!sendAlertFrame)
                {
                    return new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""{"type":"heartbeat"}"""));
                }

                await alertFrameGate.WaitAsync(cancellationToken);
                NeptunRealtimeMessage message = new(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("""
                    {"type":"alerts","data":{"oblasts":[{"key":"poltavska","name":"Полтавська область","oblast":""}],"raions":[{"key":"poltavska:kremenchutskyi","name":"Кременчуцький район","oblast":"Полтавська область"}]}}
                    """));
                AlertApplied.TrySetResult();
                return message;
            }

            if (closeAfterDelay)
            {
                await Task.Delay(TimeSpan.FromSeconds(15.1), cancellationToken);
                return new(WebSocketMessageType.Close, [], WebSocketCloseStatus.NormalClosure);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Очікування має завершитися скасуванням.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OversizedStream(int remainingBytes) : Stream
    {
        private int _remainingBytes = remainingBytes;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var bytesRead = Math.Min(count, _remainingBytes);
            Array.Clear(buffer, offset, bytesRead);
            _remainingBytes -= bytesRead;
            return bytesRead;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytesRead = Math.Min(buffer.Length, _remainingBytes);
            buffer.Span[..bytesRead].Clear();
            _remainingBytes -= bytesRead;
            return ValueTask.FromResult(bytesRead);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
