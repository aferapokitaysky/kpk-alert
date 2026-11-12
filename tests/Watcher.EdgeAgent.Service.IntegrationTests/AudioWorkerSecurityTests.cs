using System.Security.AccessControl;
using System.Security.Principal;
using Watcher.EdgeAgent.AudioWorker;

namespace Watcher.EdgeAgent.Service.IntegrationTests;

public sealed class AudioWorkerSecurityTests
{
    [Fact]
    public async Task Supervisor_RestartsWorkerAfterExitWithBoundedBackoff()
    {
        using CancellationTokenSource cancellation = new();
        var starts = 0;
        List<TimeSpan> delays = [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Watcher.EdgeAgent.AudioWorker.Program.RunSupervisionLoopAsync(
            _ =>
            {
                if (Interlocked.Increment(ref starts) == 1) throw new IOException("Simulated worker exit.");
                return Task.CompletedTask;
            },
            (delay, token) =>
            {
                delays.Add(delay);
                if (delays.Count == 2)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                }
                return Task.CompletedTask;
            },
            cancellation.Token));

        Assert.Equal(2, starts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(100, 30)]
    public void SupervisorRestartDelay_IsBounded(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Watcher.EdgeAgent.AudioWorker.Program.GetRestartDelay(failures));
    }

    [Fact]
    public void PipeAcl_AllowsOnlyServiceReadWriteWithoutCreatingServerInstances()
    {
        SecurityIdentifier serviceSid = new("S-1-5-80-111-222-333-444-555");

        var accessSddl = AudioWorkerPipeServer.GetPipeAccessSddl(serviceSid);

        Assert.Contains(serviceSid.Value, accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain("0x4", accessSddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 5, false)]
    [InlineData(0, 0, false)]
    [InlineData(4, uint.MaxValue, false)]
    public void SessionValidation_UsesOnlyActiveConsoleSession(int workerSessionId, uint activeSessionId, bool expected)
    {
        Assert.Equal(expected, AudioWorkerPipeServer.IsAllowedSession(workerSessionId, activeSessionId));
    }
}
