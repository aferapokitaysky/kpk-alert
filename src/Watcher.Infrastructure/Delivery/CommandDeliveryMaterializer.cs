using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Infrastructure.Delivery;

public enum OutboxMaterializationOutcome
{
    Materialized,
    AlreadyProcessed,
    NotFound,
    UnsupportedEvent
}

public sealed record OutboxMaterializationResult(
    OutboxMaterializationOutcome Outcome,
    int DeliveryCount = 0,
    int ExpiredDeliveryCount = 0);

public sealed class CommandDeliveryMaterializer(WatcherDbContext db, TimeProvider clock)
{
    public const string CommandCreatedEvent = "alert_command.created";

    public async Task<OutboxMaterializationResult> MaterializeAsync(
        Guid outboxMessageId,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        OutboxMessage? outbox = await LoadAndLockOutboxRowAsync(outboxMessageId, cancellationToken);
        if (outbox is null)
        {
            return new OutboxMaterializationResult(OutboxMaterializationOutcome.NotFound);
        }

        if (outbox.ProcessedAtUtc is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return new OutboxMaterializationResult(OutboxMaterializationOutcome.AlreadyProcessed);
        }

        if (!string.Equals(outbox.EventType, CommandCreatedEvent, StringComparison.Ordinal))
        {
            return new OutboxMaterializationResult(OutboxMaterializationOutcome.UnsupportedEvent);
        }

        AlertCommand command = await db.AlertCommands.AsNoTracking()
            .SingleAsync(item => item.Id == outbox.CommandId, cancellationToken);

        // Набір вузлів STOP зафіксований API разом із командою, щоб outbox не додав нові пристрої.
        Guid[] eligibleDeviceIds = command.Action == "stop"
            ? []
            : await (
                from target in db.CommandTargets.AsNoTracking()
                join device in db.Devices.AsNoTracking() on target.LocationId equals device.LocationId
                join location in db.Locations.AsNoTracking() on target.LocationId equals location.Id
                where target.CommandId == command.Id && location.IsActive && device.RevokedAtUtc == null
                select device.Id)
                .Distinct()
                .OrderBy(deviceId => deviceId)
                .ToArrayAsync(cancellationToken);

        HashSet<Guid> existingDeviceIds = await db.CommandDeliveries.AsNoTracking()
            .Where(delivery => delivery.CommandId == command.Id)
            .Select(delivery => delivery.DeviceId)
            .ToHashSetAsync(cancellationToken);

        DateTimeOffset now = clock.GetUtcNow();
        var isExpired = now >= command.ExpiresAtUtc;
        CommandDelivery[] deliveries = eligibleDeviceIds
            .Where(deviceId => !existingDeviceIds.Contains(deviceId))
            .Select(deviceId => new CommandDelivery
            {
                Id = Guid.NewGuid(),
                CommandId = command.Id,
                DeviceId = deviceId,
                State = isExpired ? CommandDeliveryState.Expired : CommandDeliveryState.Pending,
                CreatedAtUtc = now,
                ExpiresAtUtc = command.ExpiresAtUtc,
                NextAttemptAtUtc = isExpired ? null : now
            })
            .ToArray();

        db.CommandDeliveries.AddRange(deliveries);
        outbox.ProcessedAtUtc = now;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "alert_command.deliveries_materialized",
            ActorType = "system",
            ActorId = null,
            Payload = JsonSerializer.Serialize(new
            {
                CommandId = command.Id,
                OutboxMessageId = outbox.Id,
                DeliveryCount = deliveries.Length,
                ExpiredDeliveryCount = deliveries.Count(item => item.State == CommandDeliveryState.Expired)
            }),
            OccurredAtUtc = now
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new OutboxMaterializationResult(
            OutboxMaterializationOutcome.Materialized,
            deliveries.Length,
            deliveries.Count(item => item.State == CommandDeliveryState.Expired));
    }

    private async Task<OutboxMessage?> LoadAndLockOutboxRowAsync(
        Guid outboxMessageId,
        CancellationToken cancellationToken)
    {
        List<OutboxMessage> messages = await db.OutboxMessages
            .FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE \"Id\" = {outboxMessageId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return messages.SingleOrDefault();
    }
}
