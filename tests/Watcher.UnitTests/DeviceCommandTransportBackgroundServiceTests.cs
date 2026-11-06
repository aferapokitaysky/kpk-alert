using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.Delivery;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class DeviceCommandTransportBackgroundServiceTests
{
    [Fact]
    public async Task Worker_retries_after_scope_creation_failure_and_continues_on_next_cycle()
    {
        var options = new DeviceCommandTransportOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 5
        };
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<WatcherDbContext>(builder =>
            builder.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        services.AddSingleton(new DeviceCommandSessionRegistry());
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(options));
        services.AddScoped<DeviceCommandDispatchBatchProcessor>();
        using ServiceProvider provider = services.BuildServiceProvider();

        var thirdScopeCreated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scopeFactory = new FailOnceScopeFactory(
            provider.GetRequiredService<IServiceScopeFactory>(),
            thirdScopeCreated);
        var service = new DeviceCommandTransportBackgroundService(
            scopeFactory,
            TimeProvider.System,
            Options.Create(options),
            NullLogger<DeviceCommandTransportBackgroundService>.Instance);

        await service.StartAsync(CancellationToken.None);
        try
        {
            await thirdScopeCreated.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.True(scopeFactory.AttemptCount >= 3);
        Assert.Equal(1, scopeFactory.FailedAttemptCount);
    }

    private sealed class FailOnceScopeFactory(
        IServiceScopeFactory innerFactory,
        TaskCompletionSource thirdScopeCreated) : IServiceScopeFactory
    {
        public int AttemptCount { get; private set; }
        public int FailedAttemptCount { get; private set; }

        public IServiceScope CreateScope()
        {
            AttemptCount++;
            if (AttemptCount == 1)
            {
                FailedAttemptCount++;
                throw new InvalidOperationException("Temporary scope creation failure.");
            }

            if (AttemptCount == 3)
            {
                thirdScopeCreated.TrySetResult();
            }
            return innerFactory.CreateScope();
        }
    }
}
