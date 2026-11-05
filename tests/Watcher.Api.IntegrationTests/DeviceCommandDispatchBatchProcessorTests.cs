using System.Data.Common;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.Delivery;
using Watcher.Contracts.DeviceCommands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceCommandDispatchBatchProcessorTests : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresApiFixture _fixture;

    public DeviceCommandDispatchBatchProcessorTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Process_batch_sends_pending_command_and_persists_in_flight_attempt()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);

            var processedCount = await processor.ProcessBatchAsync(CancellationToken.None);

            Assert.Equal(1, processedCount);
            Assert.Contains("\"commandId\":\"" + commandId, Encoding.UTF8.GetString(socket.Payload));
            CommandDispatch? dispatch = JsonSerializer.Deserialize<CommandDispatch>(socket.Payload);
            Assert.NotNull(dispatch);
            Assert.Equal(commandId, dispatch.CommandId);
            Assert.Equal(deviceId, dispatch.DeviceId);
            Assert.Equal("evacuation", dispatch.PresetCode);
        }
        finally
        {
            sessions.Unregister(session);
        }

        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.InFlight, delivery.State);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(Now, delivery.LastAttemptAtUtc);
        Assert.Equal(Now.AddSeconds(10), delivery.NextAttemptAtUtc);
    }

    [PostgresFact]
    public async Task Process_batch_expires_stale_delivery_without_sending_it()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (_, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(
            seed.FirstLocationId, expiresAtUtc: Now.AddSeconds(-1));
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            CommandDelivery seededDelivery = await db.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
            Assert.Equal(CommandDeliveryState.Pending, seededDelivery.State);
            Assert.True(seededDelivery.ExpiresAtUtc <= Now);
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);

            var processedCount = await processor.ProcessBatchAsync(CancellationToken.None);

            Assert.Equal(1, processedCount);
            Assert.Equal(0, socket.SendCount);
        }
        finally
        {
            sessions.Unregister(session);
        }

        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.Expired, delivery.State);
        Assert.Null(delivery.NextAttemptAtUtc);
        Assert.Contains(await verifyDb.AuditEvents.ToListAsync(), item =>
            item.EventType == "alert_command.delivery_expired");
    }

    [PostgresFact]
    public async Task Concurrent_workers_dispatch_one_delivery_only_once()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (_, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        DeviceCommandSessionRegistry firstSessions = new();
        DeviceCommandSessionRegistry secondSessions = new();
        RecordingWebSocket firstSocket = new();
        RecordingWebSocket secondSocket = new();
        DeviceCommandWebSocketSession firstSession = await firstSessions.RegisterAsync(
            deviceId, firstSocket, CancellationToken.None);
        DeviceCommandWebSocketSession secondSession = await secondSessions.RegisterAsync(
            deviceId, secondSocket, CancellationToken.None);
        try
        {
            DueDeliveryQueryBarrier barrier = new(expectedQueries: 2);
            await using WatcherDbContext firstDb = _fixture.CreateDbContext(barrier);
            await using WatcherDbContext secondDb = _fixture.CreateDbContext(barrier);
            DeviceCommandDispatchBatchProcessor firstProcessor = CreateProcessor(firstDb, firstSessions);
            DeviceCommandDispatchBatchProcessor secondProcessor = CreateProcessor(secondDb, secondSessions);

            Task<int> firstRun = firstProcessor.ProcessBatchAsync(CancellationToken.None);
            Task<int> secondRun = secondProcessor.ProcessBatchAsync(CancellationToken.None);
            await barrier.WaitForBothQueriesAsync();
            await Task.WhenAll(firstRun, secondRun);
        }
        finally
        {
            firstSessions.Unregister(firstSession);
            secondSessions.Unregister(secondSession);
        }

        Assert.Equal(1, firstSocket.SendCount + secondSocket.SendCount);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.InFlight, delivery.State);
        Assert.Equal(1, delivery.AttemptCount);
    }

    private async Task<(Guid CommandId, Guid DeviceId, Guid DeliveryId)> AddPendingStartAsync(
        Guid locationId,
        DateTimeOffset? expiresAtUtc = null)
    {
        var commandId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        DateTimeOffset expiration = expiresAtUtc ?? Now.AddMinutes(5);

        await using WatcherDbContext db = _fixture.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            LocationId = locationId,
            Name = "Вузол транспорту команд",
            CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray())
        });
        db.AlertCommands.Add(new AlertCommand
        {
            Id = commandId,
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            MaximumPlaybackDurationSeconds = 900,
            CreatedAtUtc = Now.AddMinutes(-1),
            ExpiresAtUtc = expiration,
            Targets =
            [
                new CommandTarget
                {
                    Id = Guid.NewGuid(),
                    LocationId = locationId,
                    Sequence = 1,
                    State = "Created"
                }
            ]
        });
        db.CommandDeliveries.Add(new CommandDelivery
        {
            Id = deliveryId,
            CommandId = commandId,
            DeviceId = deviceId,
            State = CommandDeliveryState.Pending,
            CreatedAtUtc = Now.AddMinutes(-1),
            ExpiresAtUtc = expiration,
            NextAttemptAtUtc = Now,
            AttemptCount = 0
        });
        await db.SaveChangesAsync();

        return (commandId, deviceId, deliveryId);
    }

    private static DeviceCommandDispatchBatchProcessor CreateProcessor(
        WatcherDbContext db,
        DeviceCommandSessionRegistry sessions)
    {
        DeviceCommandTransportOptions options = new()
        {
            AcknowledgementTimeout = TimeSpan.FromSeconds(10),
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaximumRetryDelay = TimeSpan.FromSeconds(15),
            BatchSize = 50
        };
        return new DeviceCommandDispatchBatchProcessor(
            db,
            sessions,
            new FixedTimeProvider(Now),
            Options.Create(options),
            NullLogger<DeviceCommandDispatchBatchProcessor>.Instance);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class DueDeliveryQueryBarrier(int expectedQueries) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _allQueriesRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queryCount;

        public Task WaitForBothQueriesAsync() => _allQueriesRead.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("command_deliveries", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("alert_commands", StringComparison.OrdinalIgnoreCase))
            {
                if (Interlocked.Increment(ref _queryCount) == expectedQueries)
                {
                    _allQueriesRead.TrySetResult();
                }

                await _allQueriesRead.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private byte[] _payload = [];

        public int SendCount { get; private set; }
        public byte[] Payload => _payload;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => CloseOutputAsync(
            closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCount++;
            _payload = buffer.ToArray();
            return Task.CompletedTask;
        }
    }
}
