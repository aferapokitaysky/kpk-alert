namespace Watcher.EdgeAgent.Core;

public sealed class EdgeAgentMaintenanceCoordinator(TimeSpan? leaseDuration = null)
{
    private readonly object _sync = new();
    private readonly TimeSpan _leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(15);
    private readonly HashSet<Guid> _activePlaybackCommandIds = [];
    private TaskCompletionSource _idle = CompletedSource();
    private CancellationTokenSource? _leaseExpiration;
    private Guid? _maintenanceLeaseId;
    private int _pendingStarts;
    private int _leaseKeepAliveCount;
    private long _leaseExpirationGeneration;

    public StartLease? TryBeginStart()
    {
        lock (_sync)
        {
            if (_maintenanceLeaseId is not null)
            {
                return null;
            }

            if (_pendingStarts == 0 && _activePlaybackCommandIds.Count == 0)
            {
                _idle = NewSource();
            }

            _pendingStarts++;
            return new StartLease(this);
        }
    }

    public async Task<Guid> BeginMaintenanceAsync(TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(drainTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(_leaseDuration, TimeSpan.Zero);
        cancellationToken.ThrowIfCancellationRequested();

        var leaseId = Guid.NewGuid();
        Task idleTask;
        lock (_sync)
        {
            if (_maintenanceLeaseId is not null)
            {
                throw new InvalidOperationException("Обслуговування вузла вже запущено.");
            }

            _maintenanceLeaseId = leaseId;
            idleTask = _pendingStarts == 0 && _activePlaybackCommandIds.Count == 0
                ? Task.CompletedTask
                : _idle.Task;
        }

        try
        {
            await idleTask.WaitAsync(drainTimeout, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            StartLeaseExpiration(leaseId);
            return leaseId;
        }
        catch
        {
            ReleaseMaintenance(leaseId);
            throw;
        }
    }

    public bool ReleaseMaintenance(Guid leaseId)
    {
        CancellationTokenSource? expiration = null;
        lock (_sync)
        {
            if (_maintenanceLeaseId != leaseId)
            {
                return false;
            }

            _maintenanceLeaseId = null;
            _leaseKeepAliveCount = 0;
            _leaseExpirationGeneration++;
            expiration = _leaseExpiration;
            _leaseExpiration = null;
        }

        expiration?.Cancel();
        expiration?.Dispose();
        return true;
    }

    public bool IsMaintenanceLeaseActive(Guid leaseId)
    {
        lock (_sync)
        {
            return _maintenanceLeaseId == leaseId;
        }
    }

    public IDisposable? KeepMaintenanceLeaseAlive(Guid leaseId)
    {
        CancellationTokenSource? expiration;
        lock (_sync)
        {
            if (_maintenanceLeaseId != leaseId)
            {
                return null;
            }

            _leaseKeepAliveCount++;
            _leaseExpirationGeneration++;
            expiration = _leaseExpiration;
            _leaseExpiration = null;
        }

        expiration?.Cancel();
        expiration?.Dispose();
        return new MaintenanceLeaseKeepAlive(this, leaseId);
    }

    private void ReleaseLeaseKeepAlive(Guid leaseId)
    {
        var restartExpiration = false;
        lock (_sync)
        {
            if (_maintenanceLeaseId != leaseId || _leaseKeepAliveCount == 0)
            {
                return;
            }

            _leaseKeepAliveCount--;
            restartExpiration = _leaseKeepAliveCount == 0;
        }

        if (restartExpiration)
        {
            StartLeaseExpiration(leaseId);
        }
    }

    public void PlaybackFinished(Guid commandId)
    {
        lock (_sync)
        {
            _activePlaybackCommandIds.Remove(commandId);
            CompleteIdleIfDrained();
        }
    }

    private void TransferStartToPlayback(Guid commandId)
    {
        lock (_sync)
        {
            if (commandId == Guid.Empty || !_activePlaybackCommandIds.Add(commandId))
            {
                throw new InvalidOperationException("Ідентифікатор активного відтворення некоректний або вже використаний.");
            }

            _pendingStarts--;
            CompleteIdleIfDrained();
        }
    }

    private void EndPendingStart()
    {
        lock (_sync)
        {
            _pendingStarts--;
            CompleteIdleIfDrained();
        }
    }

    private void CompleteIdleIfDrained()
    {
        if (_pendingStarts == 0 && _activePlaybackCommandIds.Count == 0)
        {
            _idle.TrySetResult();
        }
    }

    private void StartLeaseExpiration(Guid leaseId)
    {
        CancellationTokenSource expiration;
        long generation;
        lock (_sync)
        {
            if (_maintenanceLeaseId != leaseId || _leaseKeepAliveCount > 0)
            {
                return;
            }

            _leaseExpirationGeneration++;
            generation = _leaseExpirationGeneration;
            expiration = new CancellationTokenSource();
            _leaseExpiration = expiration;
        }

        _ = ExpireLeaseAsync(leaseId, generation, expiration.Token);
    }

    private async Task ExpireLeaseAsync(Guid leaseId, long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_leaseDuration, cancellationToken);
            ExpireLease(leaseId, generation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void ExpireLease(Guid leaseId, long generation)
    {
        CancellationTokenSource? expiration = null;
        lock (_sync)
        {
            if (_maintenanceLeaseId != leaseId || _leaseKeepAliveCount > 0 ||
                _leaseExpirationGeneration != generation)
            {
                return;
            }

            _maintenanceLeaseId = null;
            _leaseExpirationGeneration++;
            expiration = _leaseExpiration;
            _leaseExpiration = null;
        }

        expiration?.Dispose();
    }

    private static TaskCompletionSource NewSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource CompletedSource()
    {
        TaskCompletionSource source = NewSource();
        source.SetResult();
        return source;
    }

    public sealed class StartLease : IDisposable
    {
        private readonly EdgeAgentMaintenanceCoordinator _owner;
        private int _state;

        internal StartLease(EdgeAgentMaintenanceCoordinator owner) => _owner = owner;

        public void MarkPlaybackStarted(Guid commandId)
        {
            if (commandId == Guid.Empty || Interlocked.CompareExchange(ref _state, 3, 0) != 0)
            {
                throw new InvalidOperationException("Дозвіл запуску вже завершено.");
            }

            try
            {
                _owner.TransferStartToPlayback(commandId);
                Volatile.Write(ref _state, 1);
            }
            catch
            {
                Volatile.Write(ref _state, 0);
                throw;
            }
        }

        public void Dispose()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 0) == 0)
            {
                _owner.EndPendingStart();
            }
        }
    }

    private sealed class MaintenanceLeaseKeepAlive(EdgeAgentMaintenanceCoordinator owner, Guid leaseId) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.ReleaseLeaseKeepAlive(leaseId);
            }
        }
    }
}
