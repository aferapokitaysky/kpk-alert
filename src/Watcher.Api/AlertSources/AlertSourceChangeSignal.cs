namespace Watcher.Api.AlertSources;

public interface IAlertSourceChangeSignal
{
    void Signal();

    Task WaitForChangeAsync(CancellationToken cancellationToken);
}

public sealed class AlertSourceChangeSignal : IAlertSourceChangeSignal
{
    private readonly object _sync = new();
    private TaskCompletionSource _wakeSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _pending;

    public void Signal()
    {
        lock (_sync)
        {
            if (_pending)
            {
                return;
            }

            _pending = true;
            _wakeSignal.TrySetResult();
        }
    }

    public async Task WaitForChangeAsync(CancellationToken cancellationToken)
    {
        Task wakeSignal;
        lock (_sync)
        {
            if (_pending)
            {
                ResetSignal();
                return;
            }

            wakeSignal = _wakeSignal.Task;
        }

        await wakeSignal.WaitAsync(cancellationToken);

        lock (_sync)
        {
            if (_wakeSignal.Task == wakeSignal)
            {
                ResetSignal();
            }
        }
    }

    private void ResetSignal()
    {
        _pending = false;
        _wakeSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
