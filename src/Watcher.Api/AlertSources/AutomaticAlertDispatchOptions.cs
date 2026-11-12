namespace Watcher.Api.AlertSources;

public sealed class AutomaticAlertDispatchOptions
{
    public const string SectionName = "AlertSources:AutomaticDispatch";
    public bool Enabled { get; init; }
    public string PresetCode { get; init; } = "air_raid";
    public Dictionary<string, string> AlertLevelPresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public int StartCommandValiditySeconds { get; init; } = 60;
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public string? GetPresetCode(string? alertLevel)
    {
        if (alertLevel is null)
        {
            return PresetCode;
        }

        return AlertLevelPresets.TryGetValue(alertLevel, out var presetCode)
            ? presetCode
            : null;
    }
}
