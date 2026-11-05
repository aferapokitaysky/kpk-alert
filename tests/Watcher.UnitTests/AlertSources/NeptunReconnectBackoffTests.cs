using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class NeptunReconnectBackoffTests
{
    [Fact]
    public void Backoff_grows_after_failed_sessions_and_resets_after_valid_alert_activity()
    {
        NeptunReconnectBackoff backoff = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.GetNextDelay(hadValidAlertActivity: false));
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.GetNextDelay(hadValidAlertActivity: false));
        Assert.Equal(TimeSpan.FromSeconds(4), backoff.GetNextDelay(hadValidAlertActivity: false));
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.GetNextDelay(hadValidAlertActivity: false));
        Assert.Equal(TimeSpan.FromSeconds(8), backoff.GetNextDelay(hadValidAlertActivity: false));

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.GetNextDelay(hadValidAlertActivity: true));
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.GetNextDelay(hadValidAlertActivity: false));
    }
}
