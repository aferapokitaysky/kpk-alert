using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Watcher.Api.Scheduling;

namespace Watcher.UnitTests;

public sealed class MinuteOfSilenceSchedulerRegistrationTests
{
    [Fact]
    public void Registration_adds_scoped_processor_and_hosted_scheduler()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);

        services.AddMinuteOfSilenceScheduler();

        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService hostedService = Assert.Single(provider.GetServices<IHostedService>());
        Assert.IsType<MinuteOfSilenceBackgroundService>(hostedService);
        ServiceDescriptor processorRegistration = Assert.Single(services, descriptor => descriptor.ServiceType == typeof(MinuteOfSilenceOccurrenceProcessor));
        Assert.Equal(ServiceLifetime.Scoped, processorRegistration.Lifetime);
    }
}
