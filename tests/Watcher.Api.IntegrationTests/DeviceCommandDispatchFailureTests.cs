using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.Delivery;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceCommandDispatchFailureTests : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresApiFixture _fixture;

    public DeviceCommandDispatchFailureTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Process_batch_fails_delivery_for_revoked_device_without_sending()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var commandId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var deliveryId = Guid.NewGuid();
        await using (WatcherDbContext setupDb = _fixture.CreateDbContext())
        {
            setupDb.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = seed.FirstLocationId,
                Name = "Відкликаний вузол",
                CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
                RevokedAtUtc = Now.AddMinutes(-1)
            });
            setupDb.AlertCommands.Add(new AlertCommand
            {
                Id = commandId,
                IdempotencyKey = Guid.NewGuid(),
                Action = "start",
                PresetCode = "evacuation",
                MaximumPlaybackDurationSeconds = 900,
                CreatedAtUtc = Now.AddMinutes(-1),
                ExpiresAtUtc = Now.AddMinutes(5),
                Targets =
                [
                    new CommandTarget
                    {
                        Id = Guid.NewGuid(),
                        LocationId = seed.FirstLocationId,
                        Sequence = 1,
                        State = "Created"
                    }
                ]
            });
            setupDb.CommandDeliveries.Add(new CommandDelivery
            {
                Id = deliveryId,
                CommandId = commandId,
                DeviceId = deviceId,
                State = CommandDeliveryState.Pending,
                CreatedAtUtc = Now.AddMinutes(-1),
                ExpiresAtUtc = Now.AddMinutes(5),
                NextAttemptAtUtc = Now
            });
            await setupDb.SaveChangesAsync();
        }

        var presenceSessionId = Guid.NewGuid();
        await using (WatcherDbContext setupDb = _fixture.CreateDbContext())
        {
            setupDb.DeviceSessions.Add(new DeviceSession
            {
                Id = presenceSessionId,
                DeviceId = deviceId,
                ConnectedAtUtc = Now.AddMinutes(-1),
                LastSeenAtUtc = Now
            });
            await setupDb.SaveChangesAsync();
        }

        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = new(
                db,
                sessions,
                new FixedTimeProvider(Now),
                Options.Create(new DeviceCommandTransportOptions
                {
                    AcknowledgementTimeout = TimeSpan.FromSeconds(10),
                    InitialRetryDelay = TimeSpan.FromSeconds(1),
                    MaximumRetryDelay = TimeSpan.FromSeconds(15),
                    BatchSize = 50
                }),
                NullLogger<DeviceCommandDispatchBatchProcessor>.Instance);

            Assert.Equal(0, await processor.ProcessBatchAsync(CancellationToken.None));
        }
        finally
        {
            sessions.Unregister(session);
        }

        Assert.Equal(0, socket.SendCount);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.Failed, delivery.State);
        Assert.Null(delivery.NextAttemptAtUtc);
        AuditEvent auditEvent = await verifyDb.AuditEvents.SingleAsync(
            item => item.EventType == "alert_command.delivery_failed");
        using var payload = JsonDocument.Parse(auditEvent.Payload);
        Assert.Equal(commandId, payload.RootElement.GetProperty("CommandId").GetGuid());
        Assert.Equal(deliveryId, payload.RootElement.GetProperty("DeliveryId").GetGuid());
        Assert.Equal(deviceId, payload.RootElement.GetProperty("DeviceId").GetGuid());
        Assert.Equal(0, payload.RootElement.GetProperty("AttemptCount").GetInt32());
        Assert.Equal("Pending", payload.RootElement.GetProperty("StateBefore").GetString());
        Assert.Equal("Failed", payload.RootElement.GetProperty("StateAfter").GetString());
        Assert.Equal("device_unavailable", payload.RootElement.GetProperty("Reason").GetString());
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class RecordingWebSocket : WebSocket
    {
        public int SendCount { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;

        public override void Abort()
        {
        }

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public override void Dispose()
        {
        }

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
            return Task.CompletedTask;
        }
    }
}
