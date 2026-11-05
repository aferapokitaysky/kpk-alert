using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Commands;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Scheduling;

public enum MinuteOfSilenceProcessingResult
{
    Queued,
    Skipped,
    AlreadyProcessed
}

public sealed class MinuteOfSilenceOccurrenceProcessor(
    WatcherDbContext db,
    TimeProvider clock,
    ILocationCommandSequenceAllocator sequenceAllocator)
{
    public const string PresetCode = "minute_of_silence";
    private static readonly TimeZoneInfo KyivTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);

    public async Task<MinuteOfSilenceProcessingResult> ProcessAsync(
        DateOnly runDate,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        MinuteOfSilenceOccurrence occurrence = new()
        {
            RunDate = runDate,
            Outcome = "processing",
            ProcessedAtUtc = clock.GetUtcNow()
        };
        db.MinuteOfSilenceOccurrences.Add(occurrence);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateRunDate(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return MinuteOfSilenceProcessingResult.AlreadyProcessed;
        }

        SoundPreset? preset = await db.SoundPresets.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Code == PresetCode && item.IsPublished,
                cancellationToken);
        if (preset is null)
        {
            return await SkipAsync(occurrence, "preset_unavailable", transaction, cancellationToken);
        }
        if (!SoundPreset.HasValidManifestHash(preset.ManifestHash))
        {
            return await SkipAsync(occurrence, "preset_manifest_invalid", transaction, cancellationToken);
        }

        List<Location> locations = await db.Locations.AsNoTracking()
            .Where(location => location.IsActive)
            .OrderBy(location => location.Id)
            .ToListAsync(cancellationToken);
        if (locations.Count == 0)
        {
            return await SkipAsync(occurrence, "no_active_locations", transaction, cancellationToken);
        }

        if (!IsDueNow(runDate))
        {
            return await SkipAsync(occurrence, "outside_execution_window", transaction, cancellationToken);
        }

        DateTimeOffset createdAtUtc = clock.GetUtcNow();
        DateTimeOffset expiresAtUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, KyivTimeZone) +
                                      MinuteOfSilenceSchedule.ExecutionWindow;
        AlertCommand command = new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = preset.Code,
            InitiatedByUserId = null,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            Targets = []
        };

        foreach (Location location in locations)
        {
            command.Targets.Add(new CommandTarget
            {
                Id = Guid.NewGuid(),
                LocationId = location.Id,
                Sequence = await sequenceAllocator.AllocateAsync(location.Id, cancellationToken),
                State = "Created"
            });
        }

        var payload = JsonSerializer.Serialize(new
        {
            command.Id,
            command.Action,
            command.PresetCode,
            command.ExpiresAtUtc,
            LocationIds = command.Targets.Select(target => target.LocationId),
            ScheduledDate = runDate
        });

        db.AlertCommands.Add(command);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CommandId = command.Id,
            EventType = "alert_command.created",
            Payload = payload,
            CreatedAtUtc = createdAtUtc
        });
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "alert_command.created",
            ActorType = "system",
            ActorId = null,
            Payload = payload,
            OccurredAtUtc = createdAtUtc
        });
        occurrence.Outcome = "queued";
        occurrence.CommandId = command.Id;
        occurrence.ProcessedAtUtc = createdAtUtc;

        await db.SaveChangesAsync(cancellationToken);
        if (!IsDueNow(runDate))
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            return await RecordSkippedAsync(runDate, "outside_execution_window", cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return MinuteOfSilenceProcessingResult.Queued;
    }

    private async Task<MinuteOfSilenceProcessingResult> SkipAsync(
        MinuteOfSilenceOccurrence occurrence,
        string reason,
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        occurrence.Outcome = "skipped";
        occurrence.SkipReason = reason;
        occurrence.ProcessedAtUtc = clock.GetUtcNow();
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "minute_of_silence.skipped",
            ActorType = "system",
            ActorId = null,
            Payload = JsonSerializer.Serialize(new { occurrence.RunDate, reason }),
            OccurredAtUtc = occurrence.ProcessedAtUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MinuteOfSilenceProcessingResult.Skipped;
    }

    public Task<MinuteOfSilenceProcessingResult> RecordMissedAsync(
        DateOnly runDate,
        CancellationToken cancellationToken)
    {
        if (!HasMissedWindow(runDate))
        {
            throw new InvalidOperationException("Для цієї дати ще не минуло вікно розкладу.");
        }

        return RecordSkippedAsync(runDate, "missed_window", cancellationToken);
    }

    private async Task<MinuteOfSilenceProcessingResult> RecordSkippedAsync(
        DateOnly runDate,
        string reason,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        DateTimeOffset processedAtUtc = clock.GetUtcNow();
        MinuteOfSilenceOccurrence occurrence = new()
        {
            RunDate = runDate,
            Outcome = "skipped",
            SkipReason = reason,
            ProcessedAtUtc = processedAtUtc
        };
        db.MinuteOfSilenceOccurrences.Add(occurrence);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateRunDate(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            return MinuteOfSilenceProcessingResult.AlreadyProcessed;
        }

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "minute_of_silence.skipped",
            ActorType = "system",
            ActorId = null,
            Payload = JsonSerializer.Serialize(new { occurrence.RunDate, reason }),
            OccurredAtUtc = processedAtUtc
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return MinuteOfSilenceProcessingResult.Skipped;
    }

    private bool IsDueNow(DateOnly runDate)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return MinuteOfSilenceSchedule.GetLocalDate(now, KyivTimeZone) == runDate &&
               MinuteOfSilenceSchedule.IsWithinExecutionWindow(now, runDate, KyivTimeZone);
    }

    private bool HasMissedWindow(DateOnly runDate)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateOnly localDate = MinuteOfSilenceSchedule.GetLocalDate(now, KyivTimeZone);
        if (localDate > runDate)
        {
            return true;
        }

        DateTimeOffset windowEndUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, KyivTimeZone) +
                                      MinuteOfSilenceSchedule.ExecutionWindow;
        return localDate == runDate && now >= windowEndUtc;
    }

    private static bool IsDuplicateRunDate(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
            ConstraintName: "PK_minute_of_silence_occurrences"
        };
}
