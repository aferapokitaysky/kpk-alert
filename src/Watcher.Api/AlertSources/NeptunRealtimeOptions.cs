namespace Watcher.Api.AlertSources;

public sealed record NeptunRealtimeOptions
{
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan RestFallbackInterval { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan InitialReconnectDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(30);
    public int MaximumMessageBytes { get; init; } = 1024 * 1024;
}
