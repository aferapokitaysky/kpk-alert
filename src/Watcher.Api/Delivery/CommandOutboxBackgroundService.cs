using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Watcher.Api.Delivery;

public sealed class CommandOutboxBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    IOptions<CommandOutboxWorkerOptions> options,
    ILogger<CommandOutboxBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        CommandOutboxWorkerOptions workerOptions = options.Value;
        if (!workerOptions.Enabled)
        {
            logger.LogInformation("Фонове матеріалізування команд з outbox вимкнено конфігурацією.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                CommandOutboxBatchProcessor processor =
                    scope.ServiceProvider.GetRequiredService<CommandOutboxBatchProcessor>();
                var materializedCount = await processor.ProcessBatchAsync(
                    workerOptions.BatchSize,
                    stoppingToken);
                if (materializedCount > 0)
                {
                    logger.LogInformation(
                        "Матеріалізовано outbox-команд: {MaterializedCount}.",
                        materializedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Цикл матеріалізації outbox завершився помилкою; наступна спроба буде через {PollInterval}. Тип помилки: {ErrorType}.",
                    workerOptions.PollInterval,
                    exception.GetType().Name);
            }

            await Task.Delay(workerOptions.PollInterval, clock, stoppingToken);
        }
    }
}
