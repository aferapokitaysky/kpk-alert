using System.Net;

namespace Watcher.Api.AlertSources;

public interface IUkraineAlarmRefreshSignal
{
    bool TryAdmit(IPAddress? remoteAddress, DateTimeOffset now, out TimeSpan retryAfter);

    bool TrySignal(DateTimeOffset now);

    Task WaitForSignalAsync(CancellationToken cancellationToken);

    bool Consume();
}

public sealed class UkraineAlarmRefreshSignal : IUkraineAlarmRefreshSignal
{
    private static readonly TimeSpan AddressWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MinimumSignalInterval = TimeSpan.FromSeconds(5);
    private const int MaximumRequestsPerAddress = 10;
    private const int MaximumTrackedAddresses = 4096;
    private readonly object _sync = new();
    private readonly Dictionary<IPAddress, AddressWindowState> _addressWindows = [];
    private TaskCompletionSource _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DateTimeOffset _lastCleanupAtUtc;
    private DateTimeOffset _nextSignalAtUtc;
    private bool _pending;

    public bool TryAdmit(IPAddress? remoteAddress, DateTimeOffset now, out TimeSpan retryAfter)
    {
        retryAfter = AddressWindow;
        if (remoteAddress is null)
        {
            return false;
        }

        IPAddress address = remoteAddress.IsIPv4MappedToIPv6
            ? remoteAddress.MapToIPv4()
            : remoteAddress;
        lock (_sync)
        {
            RemoveExpiredAddresses(now);
            if (!_addressWindows.TryGetValue(address, out AddressWindowState? state))
            {
                if (_addressWindows.Count >= MaximumTrackedAddresses)
                {
                    return false;
                }

                _addressWindows[address] = new(now, 1);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (now - state.StartedAtUtc >= AddressWindow)
            {
                _addressWindows[address] = new(now, 1);
                retryAfter = TimeSpan.Zero;
                return true;
            }

            if (state.RequestCount >= MaximumRequestsPerAddress)
            {
                retryAfter = state.StartedAtUtc + AddressWindow - now;
                return false;
            }

            _addressWindows[address] = state with { RequestCount = state.RequestCount + 1 };
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    public bool TrySignal(DateTimeOffset now)
    {
        lock (_sync)
        {
            if (_pending || now < _nextSignalAtUtc)
            {
                return false;
            }

            _pending = true;
            _nextSignalAtUtc = now + MinimumSignalInterval;
            _wakeSignal.TrySetResult();
            return true;
        }
    }

    public async Task WaitForSignalAsync(CancellationToken cancellationToken)
    {
        Task wakeSignal;
        lock (_sync)
        {
            wakeSignal = _wakeSignal.Task;
        }

        await wakeSignal.WaitAsync(cancellationToken);

        lock (_sync)
        {
            if (_wakeSignal.Task == wakeSignal)
            {
                _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public bool Consume()
    {
        lock (_sync)
        {
            var pending = _pending;
            _pending = false;
            return pending;
        }
    }

    private void RemoveExpiredAddresses(DateTimeOffset now)
    {
        if (now - _lastCleanupAtUtc < AddressWindow)
        {
            return;
        }

        foreach (IPAddress address in _addressWindows
                     .Where(pair => now - pair.Value.StartedAtUtc >= AddressWindow)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _addressWindows.Remove(address);
        }

        _lastCleanupAtUtc = now;
    }

    private sealed record AddressWindowState(DateTimeOffset StartedAtUtc, int RequestCount);
}
