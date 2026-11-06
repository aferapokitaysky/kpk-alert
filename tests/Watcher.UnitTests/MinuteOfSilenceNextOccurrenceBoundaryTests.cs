using Watcher.Domain.Scheduling;

namespace Watcher.UnitTests;

public sealed class MinuteOfSilenceNextOccurrenceBoundaryTests
{
    private static readonly TimeZoneInfo KyivTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);

    [Fact]
    public void Next_occurrence_is_today_only_while_instant_is_strictly_before_schedule()
    {
        DateOnly localDate = new(2026, 11, 5);
        DateTimeOffset occurrence = MinuteOfSilenceSchedule.GetOccurrenceUtc(localDate, KyivTimeZone);

        DateTimeOffset justBefore = MinuteOfSilenceSchedule.GetNextOccurrenceUtc(
            occurrence.AddTicks(-1), KyivTimeZone);
        DateTimeOffset exactlyAt = MinuteOfSilenceSchedule.GetNextOccurrenceUtc(
            occurrence, KyivTimeZone);
        DateTimeOffset justAfter = MinuteOfSilenceSchedule.GetNextOccurrenceUtc(
            occurrence.AddTicks(1), KyivTimeZone);

        Assert.Equal(occurrence, justBefore);
        Assert.Equal(MinuteOfSilenceSchedule.GetOccurrenceUtc(localDate.AddDays(1), KyivTimeZone), exactlyAt);
        Assert.Equal(exactlyAt, justAfter);
    }
}
