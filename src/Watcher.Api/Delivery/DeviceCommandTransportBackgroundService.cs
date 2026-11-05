using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Watcher.Api.Delivery;

public sealed class DeviceCommandTransportBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<DeviceCommandTransportOptions> options,
    ILogger<DeviceCommandTransportBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DeviceCommandTransportOptions workerOptions = options.Value;
        if (!workerOptions.Enabled)
        {
            logger.LogInformation("Доставку команд пристроям вимкнено конфігурацією.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                DeviceCommandDispatchBatchProcessor processor =
                    scope.ServiceProvider.GetRequiredService<DeviceCommandDispatchBatchProcessor>();
                var processedCount = await processor.ProcessBatchAsync(stoppingToken);
                if (processedCount > 0)
                {
                    logger.LogDebug("Оброблено доставок команд: {ProcessedCount}.", processedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Цикл доставки команд завершився помилкою; наступна спроба через {PollInterval}. Тип помилки: {ErrorType}.",
                    workerOptions.PollInterval,
                    exception.GetType().Name);
            }

            await Task.Delay(workerOptions.PollInterval, clock, stoppingToken);
        }
    }
}
