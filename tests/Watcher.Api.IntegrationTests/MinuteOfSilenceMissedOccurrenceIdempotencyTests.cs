using Microsoft.EntityFrameworkCore;
using Watcher.Api.Commands;
using Watcher.Api.Scheduling;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class MinuteOfSilenceMissedOccurrenceIdempotencyTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public MinuteOfSilenceMissedOccurrenceIdempotencyTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Repeated_missed_window_recording_does_not_duplicate_occurrence_or_audit()
    {
        await _fixture.ResetAndSeedAsync();
        DateOnly runDate = new(2026, 11, 5);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateTimeOffset afterWindow = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, timeZone) +
                                     MinuteOfSilenceSchedule.ExecutionWindow;

        MinuteOfSilenceProcessingResult firstResult = await RecordMissedAsync(runDate, afterWindow);
        MinuteOfSilenceProcessingResult repeatedResult = await RecordMissedAsync(runDate, afterWindow);

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, firstResult);
        Assert.Equal(MinuteOfSilenceProcessingResult.AlreadyProcessed, repeatedResult);
        await using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await db.MinuteOfSilenceOccurrences.SingleAsync();
        Assert.Equal(runDate, occurrence.RunDate);
        Assert.Equal("skipped", occurrence.Outcome);
        Assert.Equal("missed_window", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Equal(1, await db.AuditEvents.CountAsync(item => item.EventType == "minute_of_silence.skipped"));
        Assert.Empty(db.AlertCommands);
        Assert.Empty(db.OutboxMessages);
    }

    private async Task<MinuteOfSilenceProcessingResult> RecordMissedAsync(
        DateOnly runDate,
        DateTimeOffset nowUtc)
    {
        await using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrenceProcessor processor = new(
            db,
            new FixedTimeProvider(nowUtc),
            new PostgresLocationCommandSequenceAllocator(db));

        return await processor.RecordMissedAsync(runDate, CancellationToken.None);
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
