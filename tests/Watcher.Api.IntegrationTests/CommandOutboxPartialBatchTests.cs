using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Api.Delivery;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Delivery;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class CommandOutboxPartialBatchTests : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly PostgresApiFixture _fixture;

    public CommandOutboxPartialBatchTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Process_batch_returns_partial_count_and_retries_the_unprocessed_row_next_time()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid[] outboxIds = await AddCommandsAsync(seed.FirstLocationId, commandCount: 2);
        FailSecondOutboxLockInterceptor interceptor = new();
        await using (WatcherDbContext db = _fixture.CreateDbContext(interceptor))
        {
            CommandDeliveryMaterializer materializer = new(db, new FixedTimeProvider(Now));
            CommandOutboxBatchProcessor processor = new(
                db, materializer, NullLogger<CommandOutboxBatchProcessor>.Instance);

            Assert.Equal(1, await processor.ProcessBatchAsync(batchSize: 2, CancellationToken.None));

            OutboxMessage first = await db.OutboxMessages.SingleAsync(item => item.Id == outboxIds[0]);
            OutboxMessage second = await db.OutboxMessages.SingleAsync(item => item.Id == outboxIds[1]);
            Assert.NotNull(first.ProcessedAtUtc);
            Assert.Null(second.ProcessedAtUtc);
            Assert.Equal(1, await db.CommandDeliveries.CountAsync());

            Assert.Equal(1, await processor.ProcessBatchAsync(batchSize: 2, CancellationToken.None));
        }

        await using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        Assert.All(await verifyDb.OutboxMessages.OrderBy(item => item.CreatedAtUtc).ToArrayAsync(),
            message => Assert.NotNull(message.ProcessedAtUtc));
        Assert.Equal(2, await verifyDb.CommandDeliveries.CountAsync());
        Assert.Equal(2, await verifyDb.AuditEvents.CountAsync(
            item => item.EventType == "alert_command.deliveries_materialized"));
    }

    private async Task<Guid[]> AddCommandsAsync(Guid locationId, int commandCount)
    {
        var deviceId = Guid.NewGuid();
        var outboxIds = new Guid[commandCount];
        await using WatcherDbContext db = _fixture.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            LocationId = locationId,
            Name = "Вузол outbox batch",
            CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray())
        });

        for (var index = 0; index < commandCount; index++)
        {
            var commandId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            DateTimeOffset createdAtUtc = Now.AddSeconds(index);
            outboxIds[index] = outboxId;
            db.AlertCommands.Add(new AlertCommand
            {
                Id = commandId,
                IdempotencyKey = Guid.NewGuid(),
                Action = "start",
                PresetCode = "evacuation",
                MaximumPlaybackDurationSeconds = 900,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = createdAtUtc.AddMinutes(5),
                Targets =
                [
                    new CommandTarget
                    {
                        Id = Guid.NewGuid(),
                        LocationId = locationId,
                        Sequence = index + 1,
                        State = "Created"
                    }
                ]
            });
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = outboxId,
                CommandId = commandId,
                EventType = CommandDeliveryMaterializer.CommandCreatedEvent,
                Payload = "{}",
                CreatedAtUtc = createdAtUtc
            });
        }

        await db.SaveChangesAsync();
        return outboxIds;
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class FailSecondOutboxLockInterceptor : DbCommandInterceptor
    {
        private int _lockCount;
        private int _failed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("outbox_messages", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase) &&
                Interlocked.Increment(ref _lockCount) == 2 &&
                Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                throw new TimeoutException("Injected failure while claiming the second outbox row.");
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
