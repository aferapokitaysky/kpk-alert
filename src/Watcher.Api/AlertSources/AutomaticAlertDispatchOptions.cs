namespace Watcher.Api.AlertSources;

public sealed class AutomaticAlertDispatchOptions
{
    public const string SectionName = "AlertSources:AutomaticDispatch";
    public bool Enabled { get; init; }
    public string PresetCode { get; init; } = "air_raid";
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);
}
