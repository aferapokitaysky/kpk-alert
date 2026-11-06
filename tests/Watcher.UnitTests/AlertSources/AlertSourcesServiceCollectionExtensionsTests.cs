using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class AlertSourcesServiceCollectionExtensionsTests
{
    [Fact]
    public void UkraineAlarm_client_rotates_connections_to_refresh_upstream_dns()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        RecordingHttpMessageHandlerBuilderFilter filter = new();
        ServiceCollection services = new();
        services.AddSingleton<IHttpMessageHandlerBuilderFilter>(filter);
        services.AddAlertSources(configuration);

        using ServiceProvider provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(AlertSourceCatalog.UkraineAlarmCode);

        SocketsHttpHandler handler = Assert.IsType<SocketsHttpHandler>(filter.PrimaryHandler);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
    }

    [Fact]
    public void Registers_webhook_signal_and_polling_worker_when_token_is_configured()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UkraineAlarm:Token"] = "test-token"
            })
            .Build();
        ServiceCollection services = new();

        services.AddAlertSources(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IUkraineAlarmRefreshSignal) &&
            descriptor.ImplementationType == typeof(UkraineAlarmRefreshSignal));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(UkraineAlarmPollingBackgroundService));
    }

    [Fact]
    public void Registers_neptun_realtime_monitor_only_when_enabled()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AlertSources:Neptun:Realtime:Enabled"] = "true",
                ["AlertSources:Neptun:Realtime:RestFallbackInterval"] = "00:00:20"
            })
            .Build();
        ServiceCollection services = new();

        services.AddAlertSources(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(NeptunRealtimeMonitor));
        using ServiceProvider provider = services.BuildServiceProvider();
        Assert.Equal(TimeSpan.FromSeconds(20),
            provider.GetRequiredService<NeptunRealtimeOptions>().RestFallbackInterval);
        Assert.Contains(provider.GetServices<IHostedService>(),
            hostedService => hostedService.GetType() == typeof(NeptunRealtimeMonitor));
    }

    [Fact]
    public void Does_not_register_neptun_realtime_monitor_by_default()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        ServiceCollection services = new();

        services.AddAlertSources(configuration);

        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(NeptunRealtimeMonitor));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(UkraineAlarmWebhookOperationsService));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType == typeof(AutomaticAlertDispatchProcessor));
    }

    [Fact]
    public void Registers_automatic_dispatch_processor_and_worker_only_when_enabled()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AlertSources:AutomaticDispatch:Enabled"] = "true"
            })
            .Build();
        ServiceCollection services = new();

        services.AddAlertSources(configuration);

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(AutomaticAlertDispatchProcessor) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(AutomaticAlertDispatchBackgroundService));
    }

    [Fact]
    public void Registers_webhook_service_without_running_provider_mutations_at_startup()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UkraineAlarm:Token"] = "test-token"
            })
            .Build();
        ServiceCollection services = new();

        services.AddAlertSources(configuration);

        ServiceDescriptor webhookRegistration = Assert.Single(services,
            descriptor => descriptor.ServiceType == typeof(UkraineAlarmWebhookRegistrationService));
        Assert.Equal(ServiceLifetime.Singleton, webhookRegistration.Lifetime);
    }

    [Fact]
    public void Rejects_invalid_neptun_realtime_options_when_monitor_is_enabled()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AlertSources:Neptun:Realtime:Enabled"] = "true",
                ["AlertSources:Neptun:Realtime:RestFallbackInterval"] = "00:00:05"
            })
            .Build();

        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddAlertSources(configuration));
    }

    private sealed class RecordingHttpMessageHandlerBuilderFilter : IHttpMessageHandlerBuilderFilter
    {
        public HttpMessageHandler? PrimaryHandler { get; private set; }

        public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
        {
            next(builder);
            if (builder.Name == AlertSourceCatalog.UkraineAlarmCode)
            {
                PrimaryHandler = builder.PrimaryHandler;
            }
        };
    }
}
