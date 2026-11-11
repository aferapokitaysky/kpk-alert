using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.UnitTests;

public sealed class EdgeAgentMaintenanceCoordinatorTests
{
    [Fact]
    public async Task BeginMaintenance_ClosesStartGateAndWaitsForPlaybackToFinish()
    {
        EdgeAgentMaintenanceCoordinator coordinator = new();
        using EdgeAgentMaintenanceCoordinator.StartLease start = Assert.IsType<EdgeAgentMaintenanceCoordinator.StartLease>(
            coordinator.TryBeginStart());
        var commandId = Guid.NewGuid();
        Task<Guid> maintenance = coordinator.BeginMaintenanceAsync(TimeSpan.FromSeconds(2), CancellationToken.None);

        Assert.Null(coordinator.TryBeginStart());
        start.MarkPlaybackStarted(commandId);
        start.Dispose();
        Assert.False(maintenance.IsCompleted);

        coordinator.PlaybackFinished(commandId);
        Guid leaseId = await maintenance;

        Assert.Null(coordinator.TryBeginStart());
        Assert.True(coordinator.ReleaseMaintenance(leaseId));
        using EdgeAgentMaintenanceCoordinator.StartLease? resumedStart = coordinator.TryBeginStart();
        Assert.NotNull(resumedStart);
    }

    [Fact]
    public async Task CancelledDrain_ReopensStartGateAndDoesNotLoseActivePlayback()
    {
        EdgeAgentMaintenanceCoordinator coordinator = new();
        using EdgeAgentMaintenanceCoordinator.StartLease start = Assert.IsType<EdgeAgentMaintenanceCoordinator.StartLease>(
            coordinator.TryBeginStart());
        var commandId = Guid.NewGuid();
        start.MarkPlaybackStarted(commandId);
        start.Dispose();
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.BeginMaintenanceAsync(TimeSpan.FromSeconds(2), cancellation.Token));

        using EdgeAgentMaintenanceCoordinator.StartLease? laterStart = coordinator.TryBeginStart();
        Assert.NotNull(laterStart);
        coordinator.PlaybackFinished(commandId);
    }

    [Fact]
    public async Task DrainTimeout_ReleasesMaintenanceRequest()
    {
        EdgeAgentMaintenanceCoordinator coordinator = new();
        using EdgeAgentMaintenanceCoordinator.StartLease start = Assert.IsType<EdgeAgentMaintenanceCoordinator.StartLease>(
            coordinator.TryBeginStart());
        var commandId = Guid.NewGuid();
        start.MarkPlaybackStarted(commandId);
        start.Dispose();

        await Assert.ThrowsAsync<TimeoutException>(
            () => coordinator.BeginMaintenanceAsync(TimeSpan.FromMilliseconds(25), CancellationToken.None));

        using EdgeAgentMaintenanceCoordinator.StartLease? laterStart = coordinator.TryBeginStart();
        Assert.NotNull(laterStart);
        coordinator.PlaybackFinished(commandId);
    }

    [Fact]
    public async Task ExpiredLease_ReopensStartGateWhenInstallerDisappears()
    {
        EdgeAgentMaintenanceCoordinator coordinator = new(TimeSpan.FromMilliseconds(25));
        Guid leaseId = await coordinator.BeginMaintenanceAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        await Task.Delay(TimeSpan.FromMilliseconds(75));

        using EdgeAgentMaintenanceCoordinator.StartLease? start = coordinator.TryBeginStart();
        Assert.NotNull(start);
        Assert.False(coordinator.ReleaseMaintenance(leaseId));
    }

    [Fact]
    public async Task LeaseKeepAlive_PreventsExpiryUntilBackupOperationFinishes()
    {
        EdgeAgentMaintenanceCoordinator coordinator = new(TimeSpan.FromMilliseconds(25));
        Guid leaseId = await coordinator.BeginMaintenanceAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        using (coordinator.KeepMaintenanceLeaseAlive(leaseId))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(75));
            Assert.True(coordinator.IsMaintenanceLeaseActive(leaseId));
            Assert.Null(coordinator.TryBeginStart());
        }

        await Task.Delay(TimeSpan.FromMilliseconds(75));
        Assert.False(coordinator.IsMaintenanceLeaseActive(leaseId));
    }
}
