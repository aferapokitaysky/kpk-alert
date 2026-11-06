namespace Watcher.EdgeAgent.Core;

public sealed class SystemEdgeAgentClock(TimeProvider timeProvider) : IEdgeAgentClock
{
    public DateTimeOffset UtcNow => timeProvider.GetUtcNow();

    public long GetTimestamp() => timeProvider.GetTimestamp();

    public TimeSpan GetElapsedTime(long startingTimestamp) => timeProvider.GetElapsedTime(startingTimestamp);
}
