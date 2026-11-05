using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public sealed class UkraineAlarmPollingBackgroundService : BackgroundService
{
    private readonly UkraineAlarmProvider _provider;
    private readonly IUkraineAlarmRefreshSignal _refreshSignal;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<UkraineAlarmPollingBackgroundService> _logger;
    private readonly TimeSpan _pollInterval;

    public UkraineAlarmPollingBackgroundService(
        UkraineAlarmProvider provider,
        IUkraineAlarmRefreshSignal refreshSignal,
        IOptions<UkraineAlarmOptions> options,
        TimeProvider timeProvider,
        ILogger<UkraineAlarmPollingBackgroundService> logger)
    {
        _provider = provider;
        _refreshSignal = refreshSignal;
        _timeProvider = timeProvider;
        _logger = logger;
        _pollInterval = options.Value.PollInterval;
        if (_pollInterval < UkraineAlarmOptions.MinimumPollInterval)
        {
            throw new InvalidOperationException("Інтервал опитування UkraineAlarm має бути не меншим за одну хвилину.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshAsync(stoppingToken);
        var lastRefreshTimestamp = _timeProvider.GetTimestamp();
        while (!stoppingToken.IsCancellationRequested)
        {
            using var waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var intervalElapsed = Task.Delay(_pollInterval, _timeProvider, waitCancellation.Token);
            Task signalReceived = _refreshSignal.WaitForSignalAsync(waitCancellation.Token);
            await Task.WhenAny(intervalElapsed, signalReceived);
            waitCancellation.Cancel();
            await ObserveWaitAsync(intervalElapsed, waitCancellation.Token);
            await ObserveWaitAsync(signalReceived, waitCancellation.Token);
            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            TimeSpan sinceLastRefresh = _timeProvider.GetElapsedTime(lastRefreshTimestamp);
            TimeSpan remainingInterval = UkraineAlarmOptions.MinimumPollInterval - sinceLastRefresh;
            if (remainingInterval > TimeSpan.Zero)
            {
                await Task.Delay(remainingInterval, _timeProvider, stoppingToken);
            }

            await RefreshAsync(stoppingToken);
            lastRefreshTimestamp = _timeProvider.GetTimestamp();
        }
    }

    private static async Task ObserveWaitAsync(Task wait, CancellationToken cancellationToken)
    {
        try
        {
            await wait;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _provider.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "Не вдалося оновити стан UkraineAlarm у фоновому опитуванні ({ExceptionType}).",
                exception.GetType().Name);
        }
    }
}
