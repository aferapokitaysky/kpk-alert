using System.Net;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class UkraineAlarmRefreshSignalTests
{
    [Fact]
    public void Limits_requests_per_address_and_normalizes_mapped_ipv4()
    {
        UkraineAlarmRefreshSignal signal = new();
        DateTimeOffset now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        var address = IPAddress.Parse("192.0.2.10");

        for (var request = 0; request < 10; request++)
        {
            Assert.True(signal.TryAdmit(address.MapToIPv6(), now, out TimeSpan retryAfter));
            Assert.Equal(TimeSpan.Zero, retryAfter);
        }

        Assert.False(signal.TryAdmit(address, now, out TimeSpan limitedRetryAfter));
        Assert.True(limitedRetryAfter > TimeSpan.Zero);
        Assert.True(signal.TryAdmit(address, now.AddMinutes(1), out _));
    }

    [Fact]
    public void Coalesces_pending_signals_and_enforces_minimum_interval()
    {
        UkraineAlarmRefreshSignal signal = new();
        DateTimeOffset now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

        Assert.True(signal.TrySignal(now));
        Assert.False(signal.TrySignal(now));
        Assert.True(signal.Consume());
        Assert.False(signal.TrySignal(now.AddSeconds(4)));
        Assert.True(signal.TrySignal(now.AddSeconds(5)));
        Assert.True(signal.Consume());
        Assert.False(signal.Consume());
    }

    [Fact]
    public async Task Accepted_signal_wakes_a_waiting_worker()
    {
        UkraineAlarmRefreshSignal signal = new();
        Task waiting = signal.WaitForSignalAsync(CancellationToken.None);

        Assert.True(signal.TrySignal(DateTimeOffset.UtcNow));
        await waiting.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Rejects_a_missing_remote_address()
    {
        UkraineAlarmRefreshSignal signal = new();

        Assert.False(signal.TryAdmit(null, DateTimeOffset.UtcNow, out TimeSpan retryAfter));

        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);
    }
}
