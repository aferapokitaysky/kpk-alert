using Microsoft.EntityFrameworkCore;
using Watcher.Api.Commands;
using Watcher.Api.Scheduling;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class MinuteOfSilenceMissedWindowGuardTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public MinuteOfSilenceMissedWindowGuardTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Record_missed_rejects_date_before_window_ends_without_persisting_rows()
    {
        await _fixture.ResetAndSeedAsync();
        DateOnly runDate = new(2026, 11, 5);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateTimeOffset occurrenceUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, timeZone);
        await using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrenceProcessor processor = new(
            db,
            new FixedTimeProvider(occurrenceUtc),
            new PostgresLocationCommandSequenceAllocator(db));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.RecordMissedAsync(runDate, CancellationToken.None));

        Assert.Equal("Для цієї дати ще не минуло вікно розкладу.", exception.Message);
        Assert.Empty(db.MinuteOfSilenceOccurrences);
        Assert.Empty(db.AuditEvents);
        Assert.Empty(db.AlertCommands);
        Assert.Empty(db.OutboxMessages);
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
