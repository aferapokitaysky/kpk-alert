using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watcher.Api.Commands;
using Watcher.Api.Scheduling;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class MinuteOfSilenceBackgroundServiceTests
{
    [Fact]
    public async Task Startup_after_daily_window_records_missed_outcome_once()
    {
        DateOnly runDate = new(2026, 11, 5);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateTimeOffset nowUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, timeZone) +
                                MinuteOfSilenceSchedule.ExecutionWindow;
        FixedTimeProvider clock = new(nowUtc);
        var databaseName = Guid.NewGuid().ToString();
        ServiceCollection services = new();
        services.AddSingleton<TimeProvider>(clock);
        services.AddDbContext<WatcherDbContext>(options => options
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        services.AddScoped<ILocationCommandSequenceAllocator, UnusedSequenceAllocator>();
        services.AddScoped<MinuteOfSilenceOccurrenceProcessor>();
        await using ServiceProvider provider = services.BuildServiceProvider();
        LoggedWarningSignal logger = new();
        MinuteOfSilenceBackgroundService service = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            clock,
            logger);

        await service.StartAsync(CancellationToken.None);
        try
        {
            Task logged = await Task.WhenAny(logger.WarningLogged.Task, logger.ErrorLogged.Task)
                .WaitAsync(TimeSpan.FromSeconds(15));
            if (logged == logger.ErrorLogged.Task)
            {
                Assert.Fail((await logger.ErrorLogged.Task)?.ToString() ?? "Background service logged an error.");
            }
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        await using AsyncServiceScope verificationScope = provider.CreateAsyncScope();
        WatcherDbContext db = verificationScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        MinuteOfSilenceOccurrence occurrence = await db.MinuteOfSilenceOccurrences.SingleAsync();
        Assert.Equal(runDate, occurrence.RunDate);
        Assert.Equal("skipped", occurrence.Outcome);
        Assert.Equal("missed_window", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Single(db.AuditEvents.Where(item => item.EventType == "minute_of_silence.skipped"));
        Assert.Empty(db.AlertCommands);
        Assert.Empty(db.OutboxMessages);
    }

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }

    private sealed class UnusedSequenceAllocator : ILocationCommandSequenceAllocator
    {
        public Task<long> AllocateAsync(Guid locationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Missed outcomes do not allocate command sequences.");
    }

    private sealed class LoggedWarningSignal : ILogger<MinuteOfSilenceBackgroundService>
    {
        public TaskCompletionSource WarningLogged { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<Exception?> ErrorLogged { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningLogged.TrySetResult();
            }
            else if (logLevel == LogLevel.Error)
            {
                ErrorLogged.TrySetResult(exception);
            }
        }
    }
}
