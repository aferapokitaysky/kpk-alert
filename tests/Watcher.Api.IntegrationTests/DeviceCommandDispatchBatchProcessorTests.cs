using System.Data.Common;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.AudioAssets;
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
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
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
        AuditEvent[] deliveryAudit = (await verifyDb.AuditEvents.ToArrayAsync())
            .Where(item => item.Payload.Contains(commandId.ToString(), StringComparison.Ordinal))
            .OrderBy(item => item.EventType)
            .ToArray();
        AuditEvent attemptStarted = Assert.Single(deliveryAudit,
            item => item.EventType == "alert_command.delivery_attempt_started");
        AuditEvent writeCompleted = Assert.Single(deliveryAudit,
            item => item.EventType == "alert_command.delivery_write_result");
        using var startedPayload = JsonDocument.Parse(attemptStarted.Payload);
        Assert.Equal("started", startedPayload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("evacuation", startedPayload.RootElement.GetProperty("PresetCode").GetString());
        using var completedPayload = JsonDocument.Parse(writeCompleted.Payload);
        Assert.Equal("completed", completedPayload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("socket_write_completed", completedPayload.RootElement.GetProperty("ReasonCode").GetString());
    }

    [PostgresFact]
    public async Task Process_batch_uses_the_manifest_hash_for_the_delivery_location()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, _) = await AddPendingStartAsync(seed.FirstLocationId);
        LocationAudioAsset asset = new()
        {
            Id = Guid.NewGuid(),
            LocationId = seed.FirstLocationId,
            PresetCode = "evacuation",
            Sha256 = new('b', 64),
            SizeBytes = 16_044,
            DurationSeconds = 1,
            CreatedAtUtc = Now
        };
        await using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.LocationAudioAssets.Add(asset);
            await db.SaveChangesAsync();
        }

        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);

            Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
        }
        finally
        {
            sessions.Unregister(session);
        }

        CommandDispatch? dispatch = JsonSerializer.Deserialize<CommandDispatch>(socket.Payload);
        Assert.NotNull(dispatch);
        Assert.Equal(commandId, dispatch.CommandId);
        Assert.Equal(DeviceAudioManifestFactory.Create(seed.FirstLocationId, asset).Sha256,
            dispatch.ManifestHash);
    }

    [PostgresFact]
    public async Task Process_batch_expires_stale_delivery_without_sending_it()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(
            seed.FirstLocationId, expiresAtUtc: Now.AddSeconds(-1));
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
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
        AuditEvent expiredEvent = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_expired")
            .ToArrayAsync());
        using var expiredPayload = JsonDocument.Parse(expiredEvent.Payload);
        Assert.Equal(deliveryId, expiredPayload.RootElement.GetProperty("DeliveryId").GetGuid());
        Assert.Equal(commandId, expiredPayload.RootElement.GetProperty("CommandId").GetGuid());
        Assert.Equal("Pending", expiredPayload.RootElement.GetProperty("StateBefore").GetString());
        Assert.Equal("Expired", expiredPayload.RootElement.GetProperty("StateAfter").GetString());
        Assert.Equal("command_expired", expiredPayload.RootElement.GetProperty("Reason").GetString());
    }

    [PostgresFact]
    public async Task Process_batch_audits_missing_result_before_retrying_delivery()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        await using (WatcherDbContext setupDb = _fixture.CreateDbContext())
        {
            CommandDelivery delivery = await setupDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
            delivery.State = CommandDeliveryState.InFlight;
            delivery.AttemptCount = 1;
            delivery.LastAttemptAtUtc = Now.AddSeconds(-10);
            delivery.NextAttemptAtUtc = Now;
            await setupDb.SaveChangesAsync();
        }

        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            Assert.Equal(1, await CreateProcessor(db, sessions).ProcessBatchAsync(CancellationToken.None));
        }
        finally
        {
            sessions.Unregister(session);
        }

        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        AuditEvent[] writeResults = (await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync())
            .Where(item => item.Payload.Contains(commandId.ToString(), StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, writeResults.Length);
        using var recovered = JsonDocument.Parse(Assert.Single(writeResults,
            item => item.Payload.Contains("previous_attempt_result_missing", StringComparison.Ordinal)).Payload);
        Assert.Equal("indeterminate", recovered.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("previous_attempt_result_missing",
            recovered.RootElement.GetProperty("ReasonCode").GetString());
        Assert.Equal(2, (await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId)).AttemptCount);
    }

    [PostgresFact]
    public async Task Process_batch_audits_missing_result_when_delivery_expires()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, _, Guid deliveryId) = await AddPendingStartAsync(
            seed.FirstLocationId, expiresAtUtc: Now.AddSeconds(-1));
        await using (WatcherDbContext setupDb = _fixture.CreateDbContext())
        {
            CommandDelivery delivery = await setupDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
            delivery.State = CommandDeliveryState.InFlight;
            delivery.AttemptCount = 1;
            delivery.LastAttemptAtUtc = Now.AddSeconds(-10);
            delivery.NextAttemptAtUtc = Now.AddSeconds(-1);
            await setupDb.SaveChangesAsync();
        }

        await using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Assert.Equal(1, await CreateProcessor(db, new DeviceCommandSessionRegistry())
                .ProcessBatchAsync(CancellationToken.None));
        }

        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        AuditEvent result = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync(), item => item.Payload.Contains(commandId.ToString(), StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(result.Payload);
        Assert.Equal("indeterminate", payload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(CommandDeliveryState.Expired,
            (await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId)).State);
    }

    [PostgresFact]
    public async Task Process_batch_does_not_mark_completed_socket_write_as_missing()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        await using (WatcherDbContext setupDb = _fixture.CreateDbContext())
        {
            CommandDelivery delivery = await setupDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
            delivery.State = CommandDeliveryState.InFlight;
            delivery.AttemptCount = 1;
            delivery.LastAttemptAtUtc = Now.AddSeconds(-10);
            delivery.NextAttemptAtUtc = Now;
            delivery.LastWriteOutcome = "completed";
            setupDb.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "alert_command.delivery_write_result",
                ActorType = "system",
                Payload = JsonSerializer.Serialize(new
                {
                    CommandId = commandId,
                    DeliveryId = deliveryId,
                    DeviceId = deviceId,
                    AttemptCount = 1,
                    Outcome = "completed",
                    ReasonCode = "socket_write_completed"
                }),
                OccurredAtUtc = Now.AddSeconds(-10)
            });
            await setupDb.SaveChangesAsync();
        }

        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            Assert.Equal(1, await CreateProcessor(db, sessions).ProcessBatchAsync(CancellationToken.None));
        }
        finally
        {
            sessions.Unregister(session);
        }

        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        AuditEvent[] writeResults = (await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync())
            .Where(item => item.Payload.Contains(commandId.ToString(), StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, writeResults.Length);
        Assert.DoesNotContain(writeResults,
            item => item.Payload.Contains("previous_attempt_result_missing", StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Process_batch_dispatches_stop_before_an_undelivered_start()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid startCommandId, Guid deviceId, Guid startDeliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        var stopCommandId = Guid.NewGuid();
        var stopDeliveryId = Guid.NewGuid();
        await using (WatcherDbContext setupDb = _fixture.CreateDbContext())
        {
            CommandDelivery startDelivery = await setupDb.CommandDeliveries
                .SingleAsync(item => item.Id == startDeliveryId);
            startDelivery.NextAttemptAtUtc = Now.AddMinutes(1);
            setupDb.AlertCommands.Add(new AlertCommand
            {
                Id = stopCommandId,
                IdempotencyKey = Guid.NewGuid(),
                Action = "stop",
                TargetCommandId = startCommandId,
                MaximumPlaybackDurationSeconds = 0,
                CreatedAtUtc = Now,
                ExpiresAtUtc = Now.AddMinutes(5),
                Targets =
                [
                    new CommandTarget
                    {
                        Id = Guid.NewGuid(),
                        LocationId = seed.FirstLocationId,
                        Sequence = 2,
                        State = "Created"
                    }
                ]
            });
            setupDb.CommandDeliveries.Add(new CommandDelivery
            {
                Id = stopDeliveryId,
                CommandId = stopCommandId,
                DeviceId = deviceId,
                State = CommandDeliveryState.Pending,
                CreatedAtUtc = Now,
                ExpiresAtUtc = Now.AddMinutes(5),
                NextAttemptAtUtc = Now
            });
            await setupDb.SaveChangesAsync();
        }

        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);

            Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
        }
        finally
        {
            sessions.Unregister(session);
        }

        CommandDispatch? dispatch = JsonSerializer.Deserialize<CommandDispatch>(socket.Payload);
        Assert.NotNull(dispatch);
        Assert.Equal(stopCommandId, dispatch.CommandId);
        Assert.Equal(DeviceCommandAction.Stop, dispatch.Action);
        Assert.Equal(startCommandId, dispatch.TargetCommandId);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        Assert.Equal(CommandDeliveryState.InFlight,
            (await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == stopDeliveryId)).State);
    }

    [PostgresFact]
    public async Task Concurrent_workers_dispatch_one_delivery_only_once()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (_, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandSessionRegistry firstSessions = new();
        DeviceCommandSessionRegistry secondSessions = new();
        RecordingWebSocket firstSocket = new();
        RecordingWebSocket secondSocket = new();
        DeviceCommandWebSocketSession firstSession = await firstSessions.RegisterAsync(
            deviceId, presenceSessionId, firstSocket, CancellationToken.None);
        DeviceCommandWebSocketSession secondSession = await secondSessions.RegisterAsync(
            deviceId, presenceSessionId, secondSocket, CancellationToken.None);
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

    [PostgresFact]
    public async Task Reconnect_after_presence_snapshot_prevents_dispatch_to_the_superseded_socket()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (_, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        Guid oldPresenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new();
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, oldPresenceSessionId, socket, CancellationToken.None);
        DispatchQueryGate queryGate = new();
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext(queryGate);
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);
            Task<int> processing = processor.ProcessBatchAsync(CancellationToken.None);

            await queryGate.WaitForDispatchQueryAsync();
            Guid newPresenceSessionId = await _fixture.DevicePresence.OpenSessionAsync(
                deviceId, CancellationToken.None);
            Assert.NotEqual(oldPresenceSessionId, newPresenceSessionId);
            queryGate.ReleaseDispatchQuery();

            Assert.Equal(1, await processing);
        }
        finally
        {
            queryGate.ReleaseDispatchQuery();
            sessions.Unregister(session);
        }

        Assert.Equal(0, socket.SendCount);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.Pending, delivery.State);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.NotNull(delivery.NextAttemptAtUtc);
        DeviceSession[] presenceSessions = await verifyDb.DeviceSessions
            .Where(item => item.DeviceId == deviceId)
            .ToArrayAsync();
        Assert.Equal(2, presenceSessions.Length);
        Assert.Single(presenceSessions, item => item.DisconnectedAtUtc == null);
        Assert.Contains(presenceSessions, item =>
            item.Id == oldPresenceSessionId && item.DisconnectedAtUtc is not null);
        AuditEvent writeResult = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync(), item => item.Payload.Contains(deliveryId.ToString(), StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(writeResult.Payload);
        Assert.Equal("not_started", payload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("presence_session_changed", payload.RootElement.GetProperty("ReasonCode").GetString());
        AuditEvent retryScheduled = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_retry_scheduled")
            .ToArrayAsync());
        using var retryPayload = JsonDocument.Parse(retryScheduled.Payload);
        Assert.Equal("presence_session_changed", retryPayload.RootElement.GetProperty("ReasonCode").GetString());
    }

    [PostgresFact]
    public async Task Process_batch_audits_socket_write_exception_as_indeterminate()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new(failSends: true);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);

            Assert.Equal(1, await processor.ProcessBatchAsync(CancellationToken.None));
        }
        finally
        {
            sessions.Unregister(session);
        }

        Assert.Equal(1, socket.SendCount);
        Assert.Equal(WebSocketState.Aborted, socket.State);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.Pending, delivery.State);
        AuditEvent writeResult = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync(),
            item => item.EventType == "alert_command.delivery_write_result" &&
                    item.Payload.Contains(commandId.ToString(), StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(writeResult.Payload);
        Assert.Equal("indeterminate", payload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("socket_write_result_unknown", payload.RootElement.GetProperty("ReasonCode").GetString());
        AuditEvent retryScheduled = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_retry_scheduled")
            .ToArrayAsync());
        using var retryPayload = JsonDocument.Parse(retryScheduled.Payload);
        Assert.Equal("socket_write_result_unknown", retryPayload.RootElement.GetProperty("ReasonCode").GetString());
    }

    [PostgresFact]
    public async Task Send_timeout_releases_reconnect_lock_independently_of_acknowledgement_timeout()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (_, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new(blockSends: true);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        DeviceCommandTransportOptions options = new()
        {
            SendTimeout = TimeSpan.FromSeconds(1),
            AcknowledgementTimeout = TimeSpan.FromSeconds(60),
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaximumRetryDelay = TimeSpan.FromSeconds(15),
            BatchSize = 50
        };

        Task<int> processing;
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions, options);
            processing = processor.ProcessBatchAsync(CancellationToken.None);
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));

            Task<Guid> reconnect = _fixture.DevicePresence.OpenSessionAsync(
                deviceId, CancellationToken.None);
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            Assert.False(reconnect.IsCompleted);

            Assert.Equal(1, await processing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.NotEqual(presenceSessionId, await reconnect.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            sessions.Unregister(session);
        }

        Assert.Equal(WebSocketState.Aborted, socket.State);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.Pending, delivery.State);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(Now.AddSeconds(1), delivery.NextAttemptAtUtc);
        AuditEvent writeResult = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync(),
            item => item.Payload.Contains(deliveryId.ToString(), StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(writeResult.Payload);
        Assert.Equal("indeterminate", payload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("socket_write_timed_out_result_unknown",
            payload.RootElement.GetProperty("ReasonCode").GetString());
        AuditEvent retryScheduled = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_retry_scheduled")
            .ToArrayAsync());
        using var retryPayload = JsonDocument.Parse(retryScheduled.Payload);
        Assert.Equal("socket_write_timed_out_result_unknown",
            retryPayload.RootElement.GetProperty("ReasonCode").GetString());
    }

    [PostgresFact]
    public async Task Shutdown_cancellation_during_socket_write_persists_indeterminate_outcome()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        (Guid commandId, Guid deviceId, Guid deliveryId) = await AddPendingStartAsync(seed.FirstLocationId);
        Guid presenceSessionId = await AddActivePresenceAsync(deviceId);
        DeviceCommandSessionRegistry sessions = new();
        RecordingWebSocket socket = new(blockSends: true);
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, presenceSessionId, socket, CancellationToken.None);
        using CancellationTokenSource shutdown = new();
        Task<int> processing;
        try
        {
            await using WatcherDbContext db = _fixture.CreateDbContext();
            DeviceCommandDispatchBatchProcessor processor = CreateProcessor(db, sessions);
            processing = processor.ProcessBatchAsync(shutdown.Token);
            await socket.SendStarted.WaitAsync(TimeSpan.FromSeconds(5));
            shutdown.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await processing);
        }
        finally
        {
            sessions.Unregister(session);
        }

        Assert.Equal(WebSocketState.Aborted, socket.State);
        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await verifyDb.CommandDeliveries.SingleAsync(item => item.Id == deliveryId);
        Assert.Equal(CommandDeliveryState.InFlight, delivery.State);
        AuditEvent writeResult = Assert.Single(await verifyDb.AuditEvents
            .Where(item => item.EventType == "alert_command.delivery_write_result")
            .ToArrayAsync(),
            item => item.Payload.Contains(commandId.ToString(), StringComparison.Ordinal));
        using var payload = JsonDocument.Parse(writeResult.Payload);
        Assert.Equal("indeterminate", payload.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal("socket_write_result_unknown", payload.RootElement.GetProperty("ReasonCode").GetString());
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
        DeviceCommandSessionRegistry sessions,
        DeviceCommandTransportOptions? transportOptions = null)
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
            Options.Create(transportOptions ?? options),
            NullLogger<DeviceCommandDispatchBatchProcessor>.Instance);
    }

    private async Task<Guid> AddActivePresenceAsync(Guid deviceId)
    {
        var sessionId = Guid.NewGuid();
        await using WatcherDbContext db = _fixture.CreateDbContext();
        db.DeviceSessions.Add(new DeviceSession
        {
            Id = sessionId,
            DeviceId = deviceId,
            ConnectedAtUtc = Now,
            LastSeenAtUtc = Now
        });
        await db.SaveChangesAsync();
        return sessionId;
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

    private sealed class DispatchQueryGate : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _queryStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseQuery = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitForDispatchQueryAsync() => _queryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void ReleaseDispatchQuery() => _releaseQuery.TrySetResult();

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("command_deliveries", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("alert_commands", StringComparison.OrdinalIgnoreCase))
            {
                _queryStarted.TrySetResult();
                await _releaseQuery.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }

    private sealed class RecordingWebSocket(bool blockSends = false, bool failSends = false) : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private byte[] _payload = [];
        private readonly TaskCompletionSource _sendStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public int SendCount { get; private set; }
        public byte[] Payload => _payload;
        public Task SendStarted => _sendStarted.Task;
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
            _sendStarted.TrySetResult();
            if (failSends)
            {
                throw new WebSocketException("Socket write failed.");
            }

            return blockSends ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : Task.CompletedTask;
        }
    }
}
