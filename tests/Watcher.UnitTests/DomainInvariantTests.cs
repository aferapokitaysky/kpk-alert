using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;

namespace Watcher.UnitTests;

public sealed class DomainInvariantTests
{
    [Fact]
    public void Manifest_hash_requires_a_64_character_hex_digest()
    {
        Assert.True(SoundPreset.HasValidManifestHash(new string('a', 64)));
        Assert.True(SoundPreset.HasValidManifestHash(new string('F', 64)));

        Assert.False(SoundPreset.HasValidManifestHash(null));
        Assert.False(SoundPreset.HasValidManifestHash(string.Empty));
        Assert.False(SoundPreset.HasValidManifestHash(new string('a', 63)));
        Assert.False(SoundPreset.HasValidManifestHash(new string('a', 65)));
        Assert.False(SoundPreset.HasValidManifestHash(new string('g', 64)));
    }

    [Fact]
    public void Schedule_rejects_a_nonexistent_nine_oclock_during_spring_transition()
    {
        TimeZoneInfo timeZone = CreateTransitionZone(
            springTransitionMonth: 3,
            springTransitionDay: 15,
            springTransitionHour: 9,
            autumnTransitionMonth: 10,
            autumnTransitionDay: 15,
            autumnTransitionHour: 18);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            MinuteOfSilenceSchedule.GetOccurrenceUtc(new DateOnly(2026, 3, 15), timeZone));

        Assert.Contains(timeZone.Id, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schedule_rejects_an_ambiguous_nine_oclock_during_autumn_transition()
    {
        TimeZoneInfo timeZone = CreateTransitionZone(
            springTransitionMonth: 3,
            springTransitionDay: 15,
            springTransitionHour: 2,
            autumnTransitionMonth: 10,
            autumnTransitionDay: 15,
            autumnTransitionHour: 10);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() =>
            MinuteOfSilenceSchedule.GetOccurrenceUtc(new DateOnly(2026, 10, 15), timeZone));

        Assert.Contains(timeZone.Id, exception.Message, StringComparison.Ordinal);
    }

    private static TimeZoneInfo CreateTransitionZone(
        int springTransitionMonth,
        int springTransitionDay,
        int springTransitionHour,
        int autumnTransitionMonth,
        int autumnTransitionDay,
        int autumnTransitionHour)
    {
        var spring = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, springTransitionHour, 0, 0),
            springTransitionMonth,
            springTransitionDay);
        var autumn = TimeZoneInfo.TransitionTime.CreateFixedDateRule(
            new DateTime(1, 1, 1, autumnTransitionHour, 0, 0),
            autumnTransitionMonth,
            autumnTransitionDay);
        var adjustment = TimeZoneInfo.AdjustmentRule.CreateAdjustmentRule(
            new DateTime(2026, 1, 1),
            new DateTime(2026, 12, 31),
            TimeSpan.FromHours(1),
            spring,
            autumn);

        return TimeZoneInfo.CreateCustomTimeZone(
            id: "DomainInvariantTests/NonStandardTransitions",
            baseUtcOffset: TimeSpan.Zero,
            displayName: "Domain test zone",
            standardDisplayName: "Standard",
            daylightDisplayName: "Daylight",
            adjustmentRules: [adjustment]);
    }
}
