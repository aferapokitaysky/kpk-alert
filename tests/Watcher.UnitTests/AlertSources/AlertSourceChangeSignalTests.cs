using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class AlertSourceChangeSignalTests
{
    [Fact]
    public async Task Coalesces_bursts_and_wakes_again_for_a_later_change()
    {
        var signal = new AlertSourceChangeSignal();
        signal.Signal();
        signal.Signal();
        signal.Signal();

        using (CancellationTokenSource firstWaitTimeout = new(TimeSpan.FromSeconds(1)))
        {
            await signal.WaitForChangeAsync(firstWaitTimeout.Token);
        }

        using (CancellationTokenSource noDuplicateTimeout = new(TimeSpan.FromMilliseconds(50)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => signal.WaitForChangeAsync(noDuplicateTimeout.Token));
        }

        signal.Signal();
        using CancellationTokenSource nextWaitTimeout = new(TimeSpan.FromSeconds(1));
        await signal.WaitForChangeAsync(nextWaitTimeout.Token);
    }

    [Fact]
    public async Task Cancelled_wait_does_not_consume_a_later_change()
    {
        var signal = new AlertSourceChangeSignal();
        using (CancellationTokenSource cancelledWait = new())
        {
            cancelledWait.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => signal.WaitForChangeAsync(cancelledWait.Token));
        }

        signal.Signal();
        using CancellationTokenSource waitTimeout = new(TimeSpan.FromSeconds(1));
        await signal.WaitForChangeAsync(waitTimeout.Token);
    }
}
