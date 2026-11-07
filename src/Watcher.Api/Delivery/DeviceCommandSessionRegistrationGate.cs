namespace Watcher.Api.Delivery;

internal sealed class DeviceCommandSessionRegistrationGate
{
    private readonly object _sync = new();
    private readonly Dictionary<Guid, GateEntry> _entries = [];

    internal int EntryCount
    {
        get
        {
            lock (_sync)
            {
                return _entries.Count;
            }
        }
    }

    internal async ValueTask<IDisposable> EnterAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        GateEntry entry;
        lock (_sync)
        {
            if (!_entries.TryGetValue(deviceId, out entry!))
            {
                entry = new GateEntry();
                _entries.Add(deviceId, entry);
            }

            entry.ReferenceCount++;
        }

        var semaphoreAcquired = false;
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
            semaphoreAcquired = true;
            return new GateLease(this, deviceId, entry);
        }
        catch
        {
            if (semaphoreAcquired)
            {
                entry.Semaphore.Release();
            }

            ReleaseReference(deviceId, entry);
            throw;
        }
    }

    private void Exit(Guid deviceId, GateEntry entry)
    {
        entry.Semaphore.Release();
        ReleaseReference(deviceId, entry);
    }

    private void ReleaseReference(Guid deviceId, GateEntry entry)
    {
        lock (_sync)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount == 0 && _entries.TryGetValue(deviceId, out GateEntry? current) &&
                ReferenceEquals(current, entry))
            {
                _entries.Remove(deviceId);
            }
        }
    }

    private sealed class GateEntry
    {
        public SemaphoreSlim Semaphore { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
    }

    private sealed class GateLease(
        DeviceCommandSessionRegistrationGate owner,
        Guid deviceId,
        GateEntry entry) : IDisposable
    {
        private DeviceCommandSessionRegistrationGate? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit(deviceId, entry);
    }
}
