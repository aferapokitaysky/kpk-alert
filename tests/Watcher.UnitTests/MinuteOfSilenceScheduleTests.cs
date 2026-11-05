using Watcher.Domain.Scheduling;

namespace Watcher.UnitTests;

public sealed class MinuteOfSilenceScheduleTests
{
    private static readonly TimeZoneInfo KyivTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);

    [Theory]
    [InlineData("2026-01-15", "2026-01-15T07:00:00+00:00")]
    [InlineData("2026-07-15", "2026-07-15T06:00:00+00:00")]
    public void Occurrence_uses_nine_oclock_in_kyiv_across_seasons(string dateText, string expectedUtc)
    {
        var date = DateOnly.Parse(dateText);

        DateTimeOffset occurrence = MinuteOfSilenceSchedule.GetOccurrenceUtc(date, KyivTimeZone);
        DateTimeOffset localOccurrence = TimeZoneInfo.ConvertTime(occurrence, KyivTimeZone);

        Assert.Equal(DateTimeOffset.Parse(expectedUtc), occurrence);
        Assert.Equal(new TimeSpan(9, 0, 0), localOccurrence.TimeOfDay);
    }

    [Fact]
    public void Occurrence_tracks_daylight_saving_transition_without_shifting_local_time()
    {
        DateTimeOffset beforeChange = MinuteOfSilenceSchedule.GetOccurrenceUtc(
            new DateOnly(2026, 3, 28), KyivTimeZone);
        DateTimeOffset afterChange = MinuteOfSilenceSchedule.GetOccurrenceUtc(
            new DateOnly(2026, 3, 29), KyivTimeZone);

        Assert.Equal(TimeSpan.FromHours(2), KyivTimeZone.GetUtcOffset(beforeChange));
        Assert.Equal(TimeSpan.FromHours(3), KyivTimeZone.GetUtcOffset(afterChange));
        Assert.Equal(new TimeSpan(9, 0, 0),
            TimeZoneInfo.ConvertTime(afterChange, KyivTimeZone).TimeOfDay);
    }

    [Fact]
    public void Execution_window_is_inclusive_at_nine_and_exclusive_at_nine_oh_one()
    {
        DateOnly date = new(2026, 11, 5);
        DateTimeOffset occurrence = MinuteOfSilenceSchedule.GetOccurrenceUtc(date, KyivTimeZone);

        Assert.True(MinuteOfSilenceSchedule.IsWithinExecutionWindow(occurrence, date, KyivTimeZone));
        Assert.True(MinuteOfSilenceSchedule.IsWithinExecutionWindow(
            occurrence.AddSeconds(59), date, KyivTimeZone));
        Assert.False(MinuteOfSilenceSchedule.IsWithinExecutionWindow(
            occurrence.AddMinutes(1), date, KyivTimeZone));
    }

    [Fact]
    public void Next_occurrence_does_not_catch_up_after_the_execution_window()
    {
        DateOnly date = new(2026, 11, 5);
        DateTimeOffset afterWindow = MinuteOfSilenceSchedule.GetOccurrenceUtc(date, KyivTimeZone)
            .AddMinutes(1);

        DateTimeOffset next = MinuteOfSilenceSchedule.GetNextOccurrenceUtc(afterWindow, KyivTimeZone);

        Assert.Equal(new DateOnly(2026, 11, 6),
            MinuteOfSilenceSchedule.GetLocalDate(next, KyivTimeZone));
        Assert.Equal(new TimeSpan(9, 0, 0), TimeZoneInfo.ConvertTime(next, KyivTimeZone).TimeOfDay);
    }
}
