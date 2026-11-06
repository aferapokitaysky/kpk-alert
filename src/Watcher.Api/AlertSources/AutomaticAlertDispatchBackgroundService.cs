using Microsoft.Extensions.DependencyInjection;

namespace Watcher.Api.AlertSources;

public sealed class AutomaticAlertDispatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    AlertSourcesStatusService statusService,
    IUkraineAlarmRefreshSignal refreshSignal,
    AutomaticAlertDispatchOptions options,
    TimeProvider timeProvider,
    ILogger<AutomaticAlertDispatchBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                AlertSourcesStatusResponse status = await statusService.GetStatusAsync(stoppingToken);
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                AutomaticAlertDispatchProcessor processor =
                    scope.ServiceProvider.GetRequiredService<AutomaticAlertDispatchProcessor>();
                AutomaticAlertDispatchResult result = await processor.ProcessAsync(status, stoppingToken);
                if (result is AutomaticAlertDispatchResult.Started or AutomaticAlertDispatchResult.Stopped)
                {
                    logger.LogInformation("Автоматичну команду оповіщення створено: {Result}.", result);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Не вдалося обробити стан автоматичного оповіщення ({ExceptionType}).",
                    exception.GetType().Name);
            }

            using var waitCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var intervalElapsed = Task.Delay(options.PollInterval, timeProvider, waitCancellation.Token);
            Task signalReceived = refreshSignal.WaitForSignalAsync(waitCancellation.Token);
            await Task.WhenAny(intervalElapsed, signalReceived);
            waitCancellation.Cancel();
            await ObserveWaitAsync(intervalElapsed, waitCancellation.Token);
            await ObserveWaitAsync(signalReceived, waitCancellation.Token);
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
}
