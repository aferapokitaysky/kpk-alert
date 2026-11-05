using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Contracts.DeviceCommands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Commands;

public enum CommandReceiptOutcome
{
    Recorded,
    Duplicate,
    NotFound,
    Conflict,
    Invalid
}

public sealed class CommandReceiptProcessor(WatcherDbContext db, TimeProvider clock)
{
    public async Task<CommandReceiptOutcome> ProcessAsync(
        Guid deviceId,
        CommandReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (DeviceCommandValidator.Validate(receipt) is not null)
        {
            return CommandReceiptOutcome.Invalid;
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        Device? device = await db.Devices
            .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null || device.RevokedAtUtc is not null ||
            !await db.Locations.AsNoTracking().AnyAsync(
                item => item.Id == device.LocationId && item.IsActive, cancellationToken))
        {
            return CommandReceiptOutcome.NotFound;
        }

        DeviceCommandReceipt? existing = await db.DeviceCommandReceipts.SingleOrDefaultAsync(
            item => item.DeviceId == deviceId && item.EventId == receipt.EventId,
            cancellationToken);
        if (existing is not null)
        {
            return IsSameReceipt(existing, receipt)
                ? CommandReceiptOutcome.Duplicate
                : CommandReceiptOutcome.Conflict;
        }

        CommandDelivery? delivery = await db.CommandDeliveries.SingleOrDefaultAsync(
            item => item.CommandId == receipt.CommandId && item.DeviceId == deviceId,
            cancellationToken);
        if (delivery is null)
        {
            return CommandReceiptOutcome.NotFound;
        }

        var expectedSequence = await (
            from target in db.CommandTargets.AsNoTracking()
            where target.CommandId == receipt.CommandId && target.LocationId == device.LocationId
            select target.Sequence)
            .SingleOrDefaultAsync(cancellationToken);
        if (expectedSequence == 0)
        {
            return CommandReceiptOutcome.NotFound;
        }

        if (expectedSequence != receipt.Sequence)
        {
            return CommandReceiptOutcome.Conflict;
        }

        AlertCommand? command = await db.AlertCommands.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == receipt.CommandId, cancellationToken);
        if (command is null || !CanTransition(command.Action, delivery.State, receipt.State))
        {
            return CommandReceiptOutcome.Conflict;
        }

        CommandDelivery? stoppedDelivery = null;
        var targetWasStopped = false;
        if (command.Action == "stop" && receipt.State == DeviceCommandState.Completed)
        {
            if (command.TargetCommandId is not { } targetCommandId)
            {
                return CommandReceiptOutcome.Conflict;
            }

            stoppedDelivery = await db.CommandDeliveries.SingleOrDefaultAsync(
                item => item.CommandId == targetCommandId && item.DeviceId == deviceId,
                cancellationToken);
            var targetSequence = await db.CommandTargets.AsNoTracking()
                .Where(item => item.CommandId == targetCommandId && item.LocationId == device.LocationId)
                .Select(item => item.Sequence)
                .SingleOrDefaultAsync(cancellationToken);
            var targetIsStart = await db.AlertCommands.AsNoTracking().AnyAsync(
                item => item.Id == targetCommandId && item.Action == "start",
                cancellationToken);
            if (stoppedDelivery is null || targetSequence == 0 || !targetIsStart ||
                targetSequence >= receipt.Sequence ||
                stoppedDelivery.State is not (CommandDeliveryState.InFlight or
                    CommandDeliveryState.Accepted or CommandDeliveryState.Playing or
                    CommandDeliveryState.Completed or CommandDeliveryState.Stopped))
            {
                return CommandReceiptOutcome.Conflict;
            }
        }

        DateTimeOffset receivedAtUtc = clock.GetUtcNow();
        db.DeviceCommandReceipts.Add(new DeviceCommandReceipt
        {
            DeviceId = deviceId,
            EventId = receipt.EventId,
            CommandId = receipt.CommandId,
            Sequence = receipt.Sequence,
            State = receipt.State.ToString(),
            OccurredAtUtc = receipt.OccurredAtUtc,
            DiagnosticCode = receipt.DiagnosticCode,
            ReceivedAtUtc = receivedAtUtc
        });
        delivery.State = ToDeliveryState(receipt.State);
        delivery.NextAttemptAtUtc = null;
        if (stoppedDelivery is not null && stoppedDelivery.State is not (
                CommandDeliveryState.Completed or CommandDeliveryState.Stopped))
        {
            stoppedDelivery.State = CommandDeliveryState.Stopped;
            stoppedDelivery.NextAttemptAtUtc = null;
            targetWasStopped = true;
        }
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "alert_command.receipt_received",
            ActorType = "device",
            ActorId = deviceId,
            OccurredAtUtc = receivedAtUtc,
            Payload = JsonSerializer.Serialize(new
            {
                receipt.CommandId,
                receipt.EventId,
                receipt.Sequence,
                State = receipt.State.ToString(),
                receipt.DiagnosticCode
            })
        });
        if (targetWasStopped && stoppedDelivery is not null)
        {
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "alert_command.delivery_stopped",
                ActorType = "device",
                ActorId = deviceId,
                OccurredAtUtc = receivedAtUtc,
                Payload = JsonSerializer.Serialize(new
                {
                    CommandId = stoppedDelivery.CommandId,
                    StopCommandId = receipt.CommandId,
                    DeviceId = deviceId
                })
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return CommandReceiptOutcome.Recorded;
    }

    private static bool IsSameReceipt(DeviceCommandReceipt existing, CommandReceipt receipt) =>
        existing.CommandId == receipt.CommandId &&
        existing.Sequence == receipt.Sequence &&
        existing.State == receipt.State.ToString() &&
        existing.OccurredAtUtc == receipt.OccurredAtUtc &&
        existing.DiagnosticCode == receipt.DiagnosticCode;

    private static bool CanTransition(CommandDeliveryState current, DeviceCommandState next) =>
        (current is CommandDeliveryState.Pending or CommandDeliveryState.InFlight &&
            next is DeviceCommandState.Accepted or DeviceCommandState.Rejected or
                DeviceCommandState.Failed or DeviceCommandState.Expired) ||
        (current == CommandDeliveryState.Accepted &&
            next is DeviceCommandState.Playing or DeviceCommandState.Failed or DeviceCommandState.Expired) ||
        (current == CommandDeliveryState.Playing &&
            next is DeviceCommandState.Completed or DeviceCommandState.Stopped or
                DeviceCommandState.Failed or DeviceCommandState.Expired);

    private static bool CanTransition(
        string action,
        CommandDeliveryState current,
        DeviceCommandState next)
    {
        if (action != "stop")
        {
            return CanTransition(current, next);
        }

        if (current is CommandDeliveryState.Pending or CommandDeliveryState.InFlight)
        {
            return next is DeviceCommandState.Accepted or DeviceCommandState.Completed or
                DeviceCommandState.Rejected or DeviceCommandState.Failed or DeviceCommandState.Expired;
        }

        return current == CommandDeliveryState.Accepted &&
            next is DeviceCommandState.Completed or DeviceCommandState.Failed or DeviceCommandState.Expired;
    }

    private static CommandDeliveryState ToDeliveryState(DeviceCommandState state) => state switch
    {
        DeviceCommandState.Accepted => CommandDeliveryState.Accepted,
        DeviceCommandState.Playing => CommandDeliveryState.Playing,
        DeviceCommandState.Completed => CommandDeliveryState.Completed,
        DeviceCommandState.Stopped => CommandDeliveryState.Stopped,
        DeviceCommandState.Rejected => CommandDeliveryState.Rejected,
        DeviceCommandState.Failed => CommandDeliveryState.Failed,
        DeviceCommandState.Expired => CommandDeliveryState.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Невідомий стан доставки команди.")
    };
}
