namespace Watcher.Api.AlertSources;

public sealed class UkraineAlarmOptions
{
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMinutes(1);

    public string? Token { get; init; }
    public string? PublicBaseUrl { get; init; }
    public TimeSpan PollInterval { get; init; } = MinimumPollInterval;
    public string PoltavaOblastName { get; init; } = "Полтавська область";
    public string KremenchukHromadaName { get; init; } = "Кременчуцька громада";
}
