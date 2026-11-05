namespace Watcher.Api.AlertSources;

internal sealed class NeptunReconnectBackoff
{
    private readonly TimeSpan _initialDelay;
    private readonly TimeSpan _maximumDelay;
    private TimeSpan _nextDelay;

    public NeptunReconnectBackoff(TimeSpan initialDelay, TimeSpan maximumDelay)
    {
        _initialDelay = initialDelay;
        _maximumDelay = maximumDelay;
        _nextDelay = initialDelay;
    }

    public TimeSpan GetNextDelay(bool hadValidAlertActivity)
    {
        if (hadValidAlertActivity)
        {
            _nextDelay = _initialDelay;
        }

        TimeSpan delay = _nextDelay;
        var doubledTicks = delay.Ticks > _maximumDelay.Ticks / 2
            ? _maximumDelay.Ticks
            : Math.Min(delay.Ticks * 2, _maximumDelay.Ticks);
        _nextDelay = TimeSpan.FromTicks(doubledTicks);
        return delay;
    }
}
