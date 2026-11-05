namespace Watcher.Domain.Scheduling;

public static class MinuteOfSilenceSchedule
{
    public const string TimeZoneId = "Europe/Kyiv";
    public static readonly TimeSpan StartTime = new(9, 0, 0);
    public static readonly TimeSpan ExecutionWindow = TimeSpan.FromMinutes(1);

    public static DateOnly GetLocalDate(DateTimeOffset instantUtc, TimeZoneInfo timeZone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instantUtc, timeZone).DateTime);

    public static DateTimeOffset GetOccurrenceUtc(DateOnly localDate, TimeZoneInfo timeZone)
    {
        var localStart = localDate.ToDateTime(TimeOnly.FromTimeSpan(StartTime), DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(localStart) || timeZone.IsAmbiguousTime(localStart))
        {
            throw new InvalidOperationException(
                $"Початок розкладу не є однозначним локальним часом у зоні {timeZone.Id}.");
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone), TimeSpan.Zero);
    }

    public static bool IsWithinExecutionWindow(
        DateTimeOffset instantUtc,
        DateOnly localDate,
        TimeZoneInfo timeZone)
    {
        DateTimeOffset occurrenceUtc = GetOccurrenceUtc(localDate, timeZone);
        return instantUtc >= occurrenceUtc && instantUtc < occurrenceUtc + ExecutionWindow;
    }

    public static DateTimeOffset GetNextOccurrenceUtc(DateTimeOffset instantUtc, TimeZoneInfo timeZone)
    {
        DateOnly localDate = GetLocalDate(instantUtc, timeZone);
        DateTimeOffset todayOccurrenceUtc = GetOccurrenceUtc(localDate, timeZone);
        return instantUtc < todayOccurrenceUtc
            ? todayOccurrenceUtc
            : GetOccurrenceUtc(localDate.AddDays(1), timeZone);
    }
}
