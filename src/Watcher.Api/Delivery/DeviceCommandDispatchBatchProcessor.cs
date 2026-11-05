using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Watcher.Api.Security;
using Watcher.Contracts.DeviceCommands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Delivery;

public sealed class DeviceCommandDispatchBatchProcessor(
    WatcherDbContext db,
    DeviceCommandSessionRegistry sessions,
    TimeProvider clock,
    IOptions<DeviceCommandTransportOptions> options,
    ILogger<DeviceCommandDispatchBatchProcessor> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = clock.GetUtcNow();
        var batchSize = options.Value.BatchSize;
        Guid[] expiredIds = await db.CommandDeliveries.AsNoTracking()
            .Where(delivery =>
                (delivery.State == CommandDeliveryState.Pending ||
                 delivery.State == CommandDeliveryState.InFlight) && delivery.ExpiresAtUtc <= now)
            .OrderBy(delivery => delivery.ExpiresAtUtc)
            .Select(delivery => delivery.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        var processedCount = 0;
        foreach (Guid deliveryId in expiredIds)
        {
            if (await ExpireAsync(deliveryId, now, cancellationToken))
            {
                processedCount++;
            }
        }

        Guid[] connectedDeviceIds = sessions.ConnectedDeviceIds.ToArray();
        if (connectedDeviceIds.Length == 0)
        {
            return processedCount;
        }

        Guid[] dueDeliveryIds = await FindDueDeliveryIdsAsync(
            connectedDeviceIds, now, batchSize, cancellationToken);
        foreach (Guid deliveryId in dueDeliveryIds)
        {
            PreparedDispatch? dispatch = await PrepareDispatchAsync(deliveryId, now, cancellationToken);
            if (dispatch is null)
            {
                continue;
            }

            var sent = await sessions.SendAsync(
                dispatch.DeviceId, dispatch.Payload, cancellationToken);
            if (!sent)
            {
                await ScheduleRetryAsync(dispatch, clock.GetUtcNow(), cancellationToken);
                logger.LogInformation(
                    "Команда {CommandId} для пристрою {DeviceId} очікує повторної доставки.",
                    dispatch.CommandId,
                    dispatch.DeviceId);
            }

            processedCount++;
        }

        return processedCount;
    }

    private async Task<Guid[]> FindDueDeliveryIdsAsync(
        Guid[] connectedDeviceIds,
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken)
    {
        IQueryable<Guid> dueDeliveries =
            from delivery in db.CommandDeliveries.AsNoTracking()
            join command in db.AlertCommands.AsNoTracking() on delivery.CommandId equals command.Id
            join target in db.CommandTargets.AsNoTracking() on command.Id equals target.CommandId
            where connectedDeviceIds.Contains(delivery.DeviceId) &&
                  delivery.NextAttemptAtUtc != null && delivery.NextAttemptAtUtc <= now &&
                  delivery.ExpiresAtUtc > now &&
                  (delivery.State == CommandDeliveryState.Pending ||
                   delivery.State == CommandDeliveryState.InFlight) &&
                  target.LocationId == (from device in db.Devices.AsNoTracking()
                                        where device.Id == delivery.DeviceId
                                        select device.LocationId).SingleOrDefault() &&
                  !(
                      from earlierDelivery in db.CommandDeliveries.AsNoTracking()
                      join earlierTarget in db.CommandTargets.AsNoTracking()
                          on earlierDelivery.CommandId equals earlierTarget.CommandId
                      where earlierDelivery.DeviceId == delivery.DeviceId &&
                            earlierTarget.LocationId == target.LocationId &&
                            earlierTarget.Sequence < target.Sequence &&
                            earlierDelivery.State != CommandDeliveryState.Completed &&
                            earlierDelivery.State != CommandDeliveryState.Stopped &&
                            earlierDelivery.State != CommandDeliveryState.Rejected &&
                            earlierDelivery.State != CommandDeliveryState.Failed &&
                            earlierDelivery.State != CommandDeliveryState.Expired &&
                            !(command.Action == "stop" && command.TargetCommandId == earlierDelivery.CommandId)
                      select earlierDelivery.Id
                  ).Any()
            orderby target.Sequence, delivery.NextAttemptAtUtc, delivery.DeviceId
            select delivery.Id;

        return await dueDeliveries.Take(batchSize).ToArrayAsync(cancellationToken);
    }

    private async Task<PreparedDispatch?> PrepareDispatchAsync(
        Guid deliveryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        CommandDelivery? delivery = await db.CommandDeliveries
            .FromSqlInterpolated($"SELECT * FROM command_deliveries WHERE \"Id\" = {deliveryId} FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(cancellationToken);
        if (delivery is null || !IsRetryable(delivery.State) ||
            delivery.NextAttemptAtUtc is null || delivery.NextAttemptAtUtc > now)
        {
            return null;
        }

        if (delivery.ExpiresAtUtc <= now)
        {
            MarkExpired(delivery, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        Device? device = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == delivery.DeviceId, cancellationToken);
        if (device is null || device.RevokedAtUtc is not null ||
            !await db.Locations.AsNoTracking().AnyAsync(
                item => item.Id == device.LocationId && item.IsActive, cancellationToken))
        {
            MarkFailed(delivery, now, "device_unavailable");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        AlertCommand? command = await db.AlertCommands.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == delivery.CommandId, cancellationToken);
        CommandTarget? target = await db.CommandTargets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CommandId == delivery.CommandId &&
                                          item.LocationId == device.LocationId,
                cancellationToken);
        if (command is null || target is null)
        {
            MarkFailed(delivery, now, "command_target_missing");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        CommandDispatch? commandDispatch = await CreateCommandDispatchAsync(
            command, delivery, target, now, cancellationToken);
        if (commandDispatch is null || DeviceCommandValidator.Validate(commandDispatch) is not null)
        {
            MarkFailed(delivery, now, "invalid_command");
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        delivery.State = CommandDeliveryState.InFlight;
        delivery.LastAttemptAtUtc = now;
        delivery.AttemptCount = checked(delivery.AttemptCount + 1);
        delivery.NextAttemptAtUtc = Min(
            delivery.ExpiresAtUtc,
            now + options.Value.AcknowledgementTimeout);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new(
            delivery.Id,
            delivery.DeviceId,
            command.Id,
            delivery.AttemptCount,
            JsonSerializer.SerializeToUtf8Bytes(commandDispatch, JsonOptions));
    }

    private async Task<CommandDispatch?> CreateCommandDispatchAsync(
        AlertCommand command,
        CommandDelivery delivery,
        CommandTarget target,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (string.Equals(command.Action, "start", StringComparison.Ordinal))
        {
            if (command.PresetCode is null || command.TargetCommandId is not null)
            {
                return null;
            }

            SoundPreset? preset = await db.SoundPresets.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Code == command.PresetCode && item.IsPublished,
                    cancellationToken);
            if (preset is null || !SoundPreset.HasValidManifestHash(preset.ManifestHash))
            {
                return null;
            }

            return new CommandDispatch(
                DeviceCommandValidator.CurrentSchemaVersion,
                command.Id,
                delivery.DeviceId,
                target.LocationId,
                target.Sequence,
                DeviceCommandAction.Start,
                preset.Code,
                preset.ManifestHash,
                now,
                command.ExpiresAtUtc,
                command.MaximumPlaybackDurationSeconds,
                null);
        }

        if (!string.Equals(command.Action, "stop", StringComparison.Ordinal) ||
            command.PresetCode is not null || command.TargetCommandId is not { } targetCommandId ||
            command.MaximumPlaybackDurationSeconds != 0)
        {
            return null;
        }

        CommandDelivery? targetDelivery = await db.CommandDeliveries.AsNoTracking()
            .SingleOrDefaultAsync(item => item.CommandId == targetCommandId &&
                                          item.DeviceId == delivery.DeviceId,
                cancellationToken);
        if (targetDelivery is null)
        {
            return null;
        }

        return new CommandDispatch(
            DeviceCommandValidator.CurrentSchemaVersion,
            command.Id,
            delivery.DeviceId,
            target.LocationId,
            target.Sequence,
            DeviceCommandAction.Stop,
            null,
            null,
            now,
            command.ExpiresAtUtc,
            0,
            targetCommandId);
    }

    private async Task<bool> ExpireAsync(
        Guid deliveryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        CommandDelivery? delivery = await db.CommandDeliveries
            .FromSqlInterpolated($"SELECT * FROM command_deliveries WHERE \"Id\" = {deliveryId} FOR UPDATE SKIP LOCKED")
            .SingleOrDefaultAsync(cancellationToken);
        if (delivery is null || !IsRetryable(delivery.State) || delivery.ExpiresAtUtc > now)
        {
            return false;
        }

        MarkExpired(delivery, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task ScheduleRetryAsync(
        PreparedDispatch dispatch,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        TimeSpan delay = CalculateRetryDelay(dispatch.AttemptCount);
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updated = await db.CommandDeliveries
            .Where(delivery => delivery.Id == dispatch.DeliveryId &&
                               delivery.State == CommandDeliveryState.InFlight)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.State, CommandDeliveryState.Pending)
                .SetProperty(delivery => delivery.NextAttemptAtUtc, now + delay), cancellationToken);
        if (updated > 0)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "alert_command.delivery_retry_scheduled",
                ActorType = "system",
                Payload = JsonSerializer.Serialize(new
                {
                    dispatch.CommandId,
                    dispatch.DeviceId,
                    dispatch.AttemptCount,
                    Reason = "connection_unavailable"
                }),
                OccurredAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private void MarkExpired(CommandDelivery delivery, DateTimeOffset now)
    {
        delivery.State = CommandDeliveryState.Expired;
        delivery.NextAttemptAtUtc = null;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "alert_command.delivery_expired",
            ActorType = "system",
            Payload = JsonSerializer.Serialize(new { delivery.CommandId, delivery.DeviceId }),
            OccurredAtUtc = now
        });
    }

    private void MarkFailed(CommandDelivery delivery, DateTimeOffset now, string reason)
    {
        delivery.State = CommandDeliveryState.Failed;
        delivery.NextAttemptAtUtc = null;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "alert_command.delivery_failed",
            ActorType = "system",
            Payload = JsonSerializer.Serialize(new { delivery.CommandId, delivery.DeviceId, Reason = reason }),
            OccurredAtUtc = now
        });
    }

    private TimeSpan CalculateRetryDelay(int attemptCount)
    {
        var multiplier = Math.Pow(2, Math.Min(Math.Max(attemptCount - 1, 0), 10));
        var delay = TimeSpan.FromTicks((long)(options.Value.InitialRetryDelay.Ticks * multiplier));
        return delay <= options.Value.MaximumRetryDelay ? delay : options.Value.MaximumRetryDelay;
    }

    private static bool IsRetryable(CommandDeliveryState state) =>
        state is CommandDeliveryState.Pending or CommandDeliveryState.InFlight;

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    private static JsonSerializerOptions CreateJsonOptions()
    {
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
        json.Converters.Add(new JsonStringEnumConverter());
        return json;
    }

    private sealed record PreparedDispatch(
        Guid DeliveryId,
        Guid DeviceId,
        Guid CommandId,
        int AttemptCount,
        byte[] Payload);
}
