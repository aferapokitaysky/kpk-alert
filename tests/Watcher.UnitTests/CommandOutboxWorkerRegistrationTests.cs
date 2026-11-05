using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Watcher.Api.Delivery;
using Watcher.Infrastructure.Delivery;

namespace Watcher.UnitTests;

public sealed class CommandOutboxWorkerRegistrationTests
{
    [Fact]
    public void Registration_adds_scoped_materializer_and_hosted_worker()
    {
        ServiceCollection services = new();
        IConfiguration configuration = new ConfigurationBuilder().Build();

        services.AddCommandOutboxWorker(configuration);

        ServiceDescriptor materializer = Assert.Single(
            services, descriptor => descriptor.ServiceType == typeof(CommandDeliveryMaterializer));
        Assert.Equal(ServiceLifetime.Scoped, materializer.Lifetime);
        ServiceDescriptor batchProcessor = Assert.Single(
            services, descriptor => descriptor.ServiceType == typeof(CommandOutboxBatchProcessor));
        Assert.Equal(ServiceLifetime.Scoped, batchProcessor.Lifetime);
        ServiceDescriptor hostedService = Assert.Single(
            services, descriptor => descriptor.ServiceType == typeof(IHostedService));
        Assert.Equal(typeof(CommandOutboxBackgroundService), hostedService.ImplementationType);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 10)]
    [InlineData(301, 10)]
    [InlineData(10, 0)]
    [InlineData(10, 501)]
    public void Invalid_poll_interval_or_batch_size_is_rejected(int pollIntervalSeconds, int batchSize)
    {
        CommandOutboxWorkerOptions options = new()
        {
            PollInterval = TimeSpan.FromSeconds(pollIntervalSeconds),
            BatchSize = batchSize
        };

        Assert.False(options.IsValid());
    }
}
