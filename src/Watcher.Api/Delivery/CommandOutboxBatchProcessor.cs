using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Watcher.Infrastructure.Delivery;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Delivery;

public sealed class CommandOutboxBatchProcessor(
    WatcherDbContext db,
    CommandDeliveryMaterializer materializer,
    ILogger<CommandOutboxBatchProcessor> logger)
{
    public async Task<int> ProcessBatchAsync(
        int batchSize,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);

        Guid[] pendingOutboxIds = await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.EventType == CommandDeliveryMaterializer.CommandCreatedEvent &&
                              message.ProcessedAtUtc == null)
            .OrderBy(message => message.CreatedAtUtc)
            .ThenBy(message => message.Id)
            .Select(message => message.Id)
            .Take(batchSize)
            .ToArrayAsync(cancellationToken);

        var materializedCount = 0;
        foreach (Guid outboxMessageId in pendingOutboxIds)
        {
            OutboxMaterializationResult result;
            try
            {
                result = await materializer.MaterializeAsync(outboxMessageId, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Не вдалося матеріалізувати outbox-повідомлення {OutboxMessageId}; наступна спроба буде під час наступного циклу. Тип помилки: {ErrorType}.",
                    outboxMessageId,
                    exception.GetType().Name);
                return materializedCount;
            }

            switch (result.Outcome)
            {
                case OutboxMaterializationOutcome.Materialized:
                    materializedCount++;
                    break;
                case OutboxMaterializationOutcome.AlreadyProcessed:
                    break;
                case OutboxMaterializationOutcome.NotFound:
                case OutboxMaterializationOutcome.UnsupportedEvent:
                    logger.LogWarning(
                        "Outbox-повідомлення {OutboxMessageId} не оброблено: {Outcome}.",
                        outboxMessageId,
                        result.Outcome);
                    return materializedCount;
                default:
                    throw new ArgumentOutOfRangeException(
                        nameof(result), result.Outcome, "Невідомий результат матеріалізації outbox.");
            }
        }

        return materializedCount;
    }
}
