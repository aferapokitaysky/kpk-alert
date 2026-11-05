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

        if (expectedSequence != receipt.Sequence || !CanTransition(delivery.State, receipt.State))
        {
            return CommandReceiptOutcome.Conflict;
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
