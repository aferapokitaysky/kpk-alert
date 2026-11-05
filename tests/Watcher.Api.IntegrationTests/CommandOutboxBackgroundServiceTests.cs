using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Watcher.Api.Delivery;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Delivery;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class CommandOutboxBackgroundServiceTests : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 7, 0, 0, TimeSpan.Zero);
    private readonly PostgresApiFixture _fixture;

    public CommandOutboxBackgroundServiceTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Worker_materializes_pending_commands_in_configured_batches()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid[] outboxIds = await AddCommandsAsync(seed.FirstLocationId, commandCount: 3);
        using WatcherDbContext connectionContext = _fixture.CreateDbContext();
        using IHost host = CreateWorkerHost(
            connectionContext.Database.GetConnectionString()!, batchSize: 1);

        await host.StartAsync();
        try
        {
            await WaitForMaterializationAsync(outboxIds);
        }
        finally
        {
            await host.StopAsync();
        }

        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(3, await db.CommandDeliveries.CountAsync());
        Assert.Equal(3, await db.AuditEvents.CountAsync(
            item => item.EventType == "alert_command.deliveries_materialized"));
    }

    [PostgresFact]
    public async Task Two_workers_materialize_the_same_outbox_row_once()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid[] outboxIds = await AddCommandsAsync(seed.FirstLocationId, commandCount: 1);
        using WatcherDbContext connectionContext = _fixture.CreateDbContext();
        var connectionString = connectionContext.Database.GetConnectionString()!;
        OutboxRowLockBarrierInterceptor barrier = new();
        using IHost firstHost = CreateWorkerHost(connectionString, batchSize: 1, barrier);
        using IHost secondHost = CreateWorkerHost(connectionString, batchSize: 1, barrier);

        await Task.WhenAll(firstHost.StartAsync(), secondHost.StartAsync());
        try
        {
            await WaitForMaterializationAsync(outboxIds);
            await WaitForBarrierAsync(barrier);
        }
        finally
        {
            await Task.WhenAll(firstHost.StopAsync(), secondHost.StopAsync());
        }

        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.CommandDeliveries.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync(
            item => item.EventType == "alert_command.deliveries_materialized"));
    }

    [PostgresFact]
    public async Task Worker_retries_after_a_transient_database_error_without_logging_its_message()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        const string sensitivePayload = "payload-must-not-appear-in-logs";
        Guid[] outboxIds = await AddCommandsAsync(
            seed.FirstLocationId,
            commandCount: 1,
            $"{{\"diagnostic\":\"{sensitivePayload}\"}}");
        using WatcherDbContext connectionContext = _fixture.CreateDbContext();
        var loggerProvider = new CapturingLoggerProvider();
        using IHost host = CreateWorkerHost(
            connectionContext.Database.GetConnectionString()!,
            batchSize: 1,
            new FailOnceReaderInterceptor(sensitivePayload),
            loggerProvider);

        await host.StartAsync();
        try
        {
            await WaitForMaterializationAsync(outboxIds);
        }
        finally
        {
            await host.StopAsync();
        }

        var workerMessages = loggerProvider.Messages
            .Where(message => message.StartsWith("Watcher.Api.Delivery.", StringComparison.Ordinal))
            .ToArray();
        Assert.True(
            workerMessages.Any(message => message.Contains("InvalidOperationException", StringComparison.Ordinal)),
            string.Join(Environment.NewLine, workerMessages));
        Assert.DoesNotContain(workerMessages, message => message.Contains(sensitivePayload, StringComparison.Ordinal));
    }

    private IHost CreateWorkerHost(
        string connectionString,
        int batchSize,
        DbCommandInterceptor? interceptor = null,
        ILoggerProvider? loggerProvider = null)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CommandOutboxWorker:Enabled"] = "true"
            })
            .Build();

        return Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging(logging =>
                {
                    if (loggerProvider is not null)
                    {
                        logging.AddProvider(loggerProvider);
                    }
                });
                services.AddSingleton(TimeProvider.System);
                services.AddDbContext<WatcherDbContext>(options =>
                {
                    options.UseNpgsql(connectionString);
                    if (interceptor is not null)
                    {
                        options.AddInterceptors(interceptor);
                    }
                });
                services.AddCommandOutboxWorker(configuration);
                services.PostConfigure<CommandOutboxWorkerOptions>(options =>
                {
                    options.PollInterval = TimeSpan.FromMilliseconds(100);
                    options.BatchSize = batchSize;
                });
            })
            .Build();
    }

    private async Task<Guid[]> AddCommandsAsync(
        Guid locationId,
        int commandCount,
        string payload = "{}")
    {
        var outboxIds = new Guid[commandCount];
        using WatcherDbContext db = _fixture.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = Guid.NewGuid(),
            LocationId = locationId,
            Name = "Тестовий вузол",
            CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray())
        });

        for (var index = 0; index < commandCount; index++)
        {
            var commandId = Guid.NewGuid();
            var outboxId = Guid.NewGuid();
            outboxIds[index] = outboxId;
            DateTimeOffset createdAtUtc = Now.AddSeconds(index);
            db.AlertCommands.Add(new AlertCommand
            {
                Id = commandId,
                IdempotencyKey = Guid.NewGuid(),
                Action = "start",
                PresetCode = "evacuation",
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
                Payload = payload,
                CreatedAtUtc = createdAtUtc
            });
        }

        await db.SaveChangesAsync();
        return outboxIds;
    }

    private async Task WaitForMaterializationAsync(Guid[] outboxIds)
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        DateTimeOffset timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            var materializedCount = await db.OutboxMessages
                .AsNoTracking()
                .CountAsync(item => outboxIds.Contains(item.Id) && item.ProcessedAtUtc != null);
            if (materializedCount == outboxIds.Length)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("Worker не матеріалізував очікувані outbox-повідомлення за 10 секунд.");
    }

    private static async Task WaitForBarrierAsync(OutboxRowLockBarrierInterceptor barrier)
    {
        DateTimeOffset timeoutAt = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            if (barrier.ArrivalCount >= 2)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException("Обидва worker не дійшли до блокування одного outbox-рядка.");
    }

    private sealed class FailOnceReaderInterceptor(string message) : DbCommandInterceptor
    {
        private int _failed;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.CompareExchange(ref _failed, 1, 0) == 0)
            {
                throw new TimeoutException(message);
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages, categoryName);

        public void Dispose()
        {
        }
    }

    private sealed class CapturingLogger(ConcurrentQueue<string> messages, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            messages.Enqueue($"{categoryName}|{formatter(state, exception)}");
    }
}
