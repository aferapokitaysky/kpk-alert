using Watcher.Api.Delivery;

namespace Watcher.UnitTests;

public sealed class DeviceCommandSessionRegistrationGateTests
{
    [Fact]
    public async Task Entry_is_kept_until_waiter_and_owner_release_their_leases()
    {
        DeviceCommandSessionRegistrationGate gate = new();
        var deviceId = Guid.NewGuid();
        IDisposable owner = await gate.EnterAsync(deviceId, CancellationToken.None);

        ValueTask<IDisposable> waiterAcquisition = gate.EnterAsync(deviceId, CancellationToken.None);
        Assert.False(waiterAcquisition.IsCompleted);

        owner.Dispose();
        IDisposable waiter = await waiterAcquisition;
        ValueTask<IDisposable> nextAcquisition = gate.EnterAsync(deviceId, CancellationToken.None);

        Assert.False(nextAcquisition.IsCompleted);
        Assert.Equal(1, gate.EntryCount);

        waiter.Dispose();
        IDisposable next = await nextAcquisition;
        Assert.Equal(1, gate.EntryCount);

        next.Dispose();
        Assert.Equal(0, gate.EntryCount);
    }

    [Fact]
    public async Task Different_devices_acquire_independent_gates_and_entries_are_removed()
    {
        DeviceCommandSessionRegistrationGate gate = new();
        IDisposable firstDevice = await gate.EnterAsync(Guid.NewGuid(), CancellationToken.None);

        ValueTask<IDisposable> secondDeviceAcquisition = gate.EnterAsync(
            Guid.NewGuid(), CancellationToken.None);

        Assert.True(secondDeviceAcquisition.IsCompletedSuccessfully);
        IDisposable secondDevice = await secondDeviceAcquisition;
        Assert.Equal(2, gate.EntryCount);

        firstDevice.Dispose();
        secondDevice.Dispose();

        Assert.Equal(0, gate.EntryCount);
    }

    [Fact]
    public async Task Cancelled_waiter_releases_its_reference_without_removing_owner_gate()
    {
        DeviceCommandSessionRegistrationGate gate = new();
        var deviceId = Guid.NewGuid();
        IDisposable owner = await gate.EnterAsync(deviceId, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        ValueTask<IDisposable> waiterAcquisition = gate.EnterAsync(deviceId, cancellation.Token);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await waiterAcquisition);
        Assert.Equal(1, gate.EntryCount);

        owner.Dispose();
        Assert.Equal(0, gate.EntryCount);
    }
}
