using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Delivery;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class CommandDeliveryMaterializerTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public CommandDeliveryMaterializerTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Materialization_creates_one_pending_delivery_per_active_device_and_marks_outbox_processed()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        DateTimeOffset now = new(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);
        DateTimeOffset expiresAtUtc = now.AddMinutes(AlertCommand.DefaultValidityMinutes);
        Guid commandId;
        Guid outboxId;
        var firstDeviceId = Guid.NewGuid();
        var secondDeviceId = Guid.NewGuid();

        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Location inactiveLocation = await db.Locations.SingleAsync(item => item.Id == seed.SecondLocationId);
            inactiveLocation.IsActive = false;
            commandId = await AddCommandAndOutboxAsync(
                db, [seed.FirstLocationId, seed.SecondLocationId], now, expiresAtUtc);
            outboxId = await db.OutboxMessages.Select(item => item.Id).SingleAsync();
            db.Devices.AddRange(
                CreateDevice(firstDeviceId, seed.FirstLocationId, "north-1"),
                CreateDevice(secondDeviceId, seed.FirstLocationId, "north-2"),
                CreateDevice(Guid.NewGuid(), seed.FirstLocationId, "revoked", now),
                CreateDevice(Guid.NewGuid(), seed.SecondLocationId, "inactive-location"));
            await db.SaveChangesAsync();
        }

        using WatcherDbContext processingDb = _fixture.CreateDbContext();
        CommandDeliveryMaterializer materializer = new(processingDb, new FixedTimeProvider(now));
        OutboxMaterializationResult result = await materializer.MaterializeAsync(outboxId, CancellationToken.None);

        Assert.Equal(OutboxMaterializationOutcome.Materialized, result.Outcome);
        Assert.Equal(2, result.DeliveryCount);
        Assert.Equal(0, result.ExpiredDeliveryCount);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        CommandDelivery[] deliveries = await readDb.CommandDeliveries
            .Where(item => item.CommandId == commandId)
            .OrderBy(item => item.DeviceId)
            .ToArrayAsync();
        Assert.Equal(new[] { firstDeviceId, secondDeviceId }.Order(), deliveries.Select(item => item.DeviceId));
        Assert.All(deliveries, delivery =>
        {
            Assert.Equal(CommandDeliveryState.Pending, delivery.State);
            Assert.Equal(now, delivery.CreatedAtUtc);
            Assert.Equal(expiresAtUtc, delivery.ExpiresAtUtc);
            Assert.Equal(now, delivery.NextAttemptAtUtc);
            Assert.Null(delivery.LastAttemptAtUtc);
            Assert.Equal(0, delivery.AttemptCount);
        });
        OutboxMessage outbox = await readDb.OutboxMessages.SingleAsync(item => item.Id == outboxId);
        Assert.Equal(now, outbox.ProcessedAtUtc);
        AuditEvent audit = await readDb.AuditEvents.SingleAsync();
        Assert.Equal("alert_command.deliveries_materialized", audit.EventType);
        Assert.Equal("system", audit.ActorType);
        using var payload = JsonDocument.Parse(audit.Payload);
        Assert.Equal(commandId, payload.RootElement.GetProperty("CommandId").GetGuid());
        Assert.Equal(2, payload.RootElement.GetProperty("DeliveryCount").GetInt32());

        OutboxMaterializationResult replay = await materializer.MaterializeAsync(outboxId, CancellationToken.None);
        Assert.Equal(OutboxMaterializationOutcome.AlreadyProcessed, replay.Outcome);
        Assert.Equal(2, await readDb.CommandDeliveries.CountAsync());
    }

    [PostgresFact]
    public async Task Expired_command_materializes_expired_deliveries_without_retry_time()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        DateTimeOffset issuedAtUtc = new(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);
        DateTimeOffset now = issuedAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes + 1);
        var deviceId = Guid.NewGuid();
        Guid commandId;
        Guid outboxId;
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            commandId = await AddCommandAndOutboxAsync(
                db, [seed.FirstLocationId], issuedAtUtc, issuedAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes));
            outboxId = await db.OutboxMessages.Select(item => item.Id).SingleAsync();
            db.Devices.Add(CreateDevice(deviceId, seed.FirstLocationId, "north-expired"));
            await db.SaveChangesAsync();
        }

        using WatcherDbContext processingDb = _fixture.CreateDbContext();
        CommandDeliveryMaterializer materializer = new(processingDb, new FixedTimeProvider(now));
        OutboxMaterializationResult result = await materializer.MaterializeAsync(outboxId, CancellationToken.None);

        Assert.Equal(OutboxMaterializationOutcome.Materialized, result.Outcome);
        Assert.Equal(1, result.ExpiredDeliveryCount);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        CommandDelivery delivery = await readDb.CommandDeliveries.SingleAsync(item => item.CommandId == commandId);
        Assert.Equal(deviceId, delivery.DeviceId);
        Assert.Equal(CommandDeliveryState.Expired, delivery.State);
        Assert.Null(delivery.NextAttemptAtUtc);
        Assert.Equal(0, delivery.AttemptCount);
    }

    [PostgresFact]
    public async Task Concurrent_materializers_process_one_outbox_once()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        DateTimeOffset now = new(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);
        Guid commandId;
        Guid outboxId;
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            commandId = await AddCommandAndOutboxAsync(
                db, [seed.FirstLocationId], now, now.AddMinutes(AlertCommand.DefaultValidityMinutes));
            outboxId = await db.OutboxMessages.Select(item => item.Id).SingleAsync();
            db.Devices.Add(CreateDevice(Guid.NewGuid(), seed.FirstLocationId, "north-concurrent"));
            await db.SaveChangesAsync();
        }

        OutboxRowLockBarrierInterceptor barrier = new();
        using WatcherDbContext firstDb = _fixture.CreateDbContext(barrier);
        using WatcherDbContext secondDb = _fixture.CreateDbContext(barrier);
        CommandDeliveryMaterializer first = new(firstDb, new FixedTimeProvider(now));
        CommandDeliveryMaterializer second = new(secondDb, new FixedTimeProvider(now));
        OutboxMaterializationResult[] results = await Task.WhenAll(
            first.MaterializeAsync(outboxId, CancellationToken.None),
            second.MaterializeAsync(outboxId, CancellationToken.None));

        Assert.Single(results, item => item.Outcome == OutboxMaterializationOutcome.Materialized);
        Assert.Single(results, item => item.Outcome == OutboxMaterializationOutcome.AlreadyProcessed);
        Assert.Equal(2, barrier.ArrivalCount);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        Assert.Equal(1, await readDb.CommandDeliveries.CountAsync(item => item.CommandId == commandId));
        Assert.Equal(1, await readDb.AuditEvents.CountAsync());
    }

    [PostgresFact]
    public async Task Unsupported_outbox_event_is_left_unprocessed()
    {
        await _fixture.ResetAndSeedAsync();
        DateTimeOffset now = new(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);
        Guid outboxId;
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Guid commandId = await AddCommandAndOutboxAsync(
                db, [], now, now.AddMinutes(AlertCommand.DefaultValidityMinutes), "other.event");
            outboxId = await db.OutboxMessages.Select(item => item.Id).SingleAsync();
        }

        using WatcherDbContext processingDb = _fixture.CreateDbContext();
        CommandDeliveryMaterializer materializer = new(processingDb, new FixedTimeProvider(now));
        OutboxMaterializationResult result = await materializer.MaterializeAsync(outboxId, CancellationToken.None);

        Assert.Equal(OutboxMaterializationOutcome.UnsupportedEvent, result.Outcome);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        Assert.Null((await readDb.OutboxMessages.SingleAsync()).ProcessedAtUtc);
        Assert.Empty(readDb.CommandDeliveries);
        Assert.Empty(readDb.AuditEvents);
    }

    private static async Task<Guid> AddCommandAndOutboxAsync(
        WatcherDbContext db,
        Guid[] locationIds,
        DateTimeOffset createdAtUtc,
        DateTimeOffset expiresAtUtc,
        string eventType = CommandDeliveryMaterializer.CommandCreatedEvent)
    {
        AlertCommand command = new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            InitiatedByUserId = null,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            Targets = locationIds.Select(locationId => new CommandTarget
            {
                Id = Guid.NewGuid(),
                LocationId = locationId,
                State = "Created"
            }).ToList()
        };
        db.AlertCommands.Add(command);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CommandId = command.Id,
            EventType = eventType,
            Payload = JsonSerializer.Serialize(new { command.Id, command.ExpiresAtUtc }),
            CreatedAtUtc = createdAtUtc
        });
        await db.SaveChangesAsync();
        return command.Id;
    }

    private static Device CreateDevice(Guid id, Guid locationId, string name, DateTimeOffset? revokedAtUtc = null) => new()
    {
        Id = id,
        LocationId = locationId,
        Name = name,
        CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray()),
        RevokedAtUtc = revokedAtUtc
    };

    private sealed class FixedTimeProvider(DateTimeOffset instantUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instantUtc;
    }
}

internal sealed class OutboxRowLockBarrierInterceptor : DbCommandInterceptor
{
    private readonly TaskCompletionSource<bool> _bothCommandsReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _arrivalCount;

    public int ArrivalCount => Volatile.Read(ref _arrivalCount);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
        {
            if (Interlocked.Increment(ref _arrivalCount) == 2)
            {
                _bothCommandsReady.TrySetResult(true);
            }

            await _bothCommandsReady.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        }

        return result;
    }
}
