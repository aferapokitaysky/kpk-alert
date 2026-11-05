using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watcher.Domain.Scheduling;

namespace Watcher.Api.Scheduling;

public sealed class MinuteOfSilenceBackgroundService(
    IServiceScopeFactory scopeFactory,
    TimeProvider clock,
    ILogger<MinuteOfSilenceBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissedWindowRetryDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeZoneInfo KyivTimeZone =
        TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DateOnly? handledDate = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            DateTimeOffset now = clock.GetUtcNow();
            DateOnly localDate = MinuteOfSilenceSchedule.GetLocalDate(now, KyivTimeZone);
            DateTimeOffset occurrenceUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(localDate, KyivTimeZone);
            DateTimeOffset windowEndUtc = occurrenceUtc + MinuteOfSilenceSchedule.ExecutionWindow;

            if (now < occurrenceUtc)
            {
                await Task.Delay(occurrenceUtc - now, clock, stoppingToken);
                continue;
            }

            if (now >= windowEndUtc && handledDate != localDate)
            {
                try
                {
                    await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                    MinuteOfSilenceOccurrenceProcessor processor =
                        scope.ServiceProvider.GetRequiredService<MinuteOfSilenceOccurrenceProcessor>();
                    MinuteOfSilenceProcessingResult result =
                        await processor.RecordMissedAsync(localDate, stoppingToken);
                    handledDate = localDate;
                    logger.LogWarning(
                        "Розклад хвилини мовчання на {RunDate} пропущено: {Outcome}.",
                        localDate,
                        result);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(exception,
                        "Не вдалося записати пропуск хвилини мовчання на {RunDate}.", localDate);
                    await Task.Delay(MissedWindowRetryDelay, clock, stoppingToken);
                }

                continue;
            }

            if (handledDate == localDate)
            {
                DateTimeOffset nextOccurrenceUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(
                    localDate.AddDays(1), KyivTimeZone);
                await Task.Delay(nextOccurrenceUtc - now, clock, stoppingToken);
                handledDate = null;
                continue;
            }

            try
            {
                await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
                MinuteOfSilenceOccurrenceProcessor processor =
                    scope.ServiceProvider.GetRequiredService<MinuteOfSilenceOccurrenceProcessor>();
                MinuteOfSilenceProcessingResult result =
                    await processor.ProcessAsync(localDate, stoppingToken);
                handledDate = localDate;
                logger.LogInformation(
                    "Оброблено розклад хвилини мовчання на {RunDate}: {Outcome}.",
                    localDate,
                    result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Не вдалося обробити розклад хвилини мовчання на {RunDate}.", localDate);
                TimeSpan remainingWindow = windowEndUtc - clock.GetUtcNow();
                if (remainingWindow > TimeSpan.Zero)
                {
                    await Task.Delay(
                        remainingWindow < RetryDelay ? remainingWindow : RetryDelay,
                        clock,
                        stoppingToken);
                }
            }
        }
    }
}
