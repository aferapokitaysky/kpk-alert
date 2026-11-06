using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public static class AlertSourcesServiceCollectionExtensions
{
    private static readonly TimeSpan UkraineAlarmConnectionLifetime = TimeSpan.FromMinutes(5);

    public static IServiceCollection AddAlertSources(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var token = configuration["ALERTS_IN_UA_TOKEN"] ??
            configuration["AlertSources:AlertsInUa:Token"];
        services.AddSingleton<IOptions<AlertsInUaOptions>>(Options.Create(new AlertsInUaOptions(token)));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<AlertStatusAggregator>();
        services.AddSingleton<AutomaticAlertDispatchPolicy>();
        AutomaticAlertDispatchOptions automaticDispatchOptions = configuration
            .GetSection(AutomaticAlertDispatchOptions.SectionName)
            .Get<AutomaticAlertDispatchOptions>() ?? new AutomaticAlertDispatchOptions();
        if (automaticDispatchOptions.PollInterval < TimeSpan.FromSeconds(15))
        {
            throw new InvalidOperationException("Інтервал оцінювання автоматичного оповіщення має бути не меншим за 15 секунд.");
        }

        services.AddSingleton(automaticDispatchOptions);
        if (automaticDispatchOptions.Enabled)
        {
            services.AddScoped<AutomaticAlertDispatchProcessor>();
            services.AddHostedService<AutomaticAlertDispatchBackgroundService>();
        }

        services.AddSingleton<AlertsInUaProvider>();
        services.AddSingleton<NeptunProvider>();
        services.Configure<UkraineAlarmOptions>(configuration.GetSection("UkraineAlarm"));
        if (configuration.GetValue<bool>("AlertSources:Neptun:Realtime:Enabled"))
        {
            NeptunRealtimeOptions realtimeOptions = new();
            configuration.GetSection("AlertSources:Neptun:Realtime").Bind(realtimeOptions);
            services.AddNeptunRealtimeMonitor(realtimeOptions);
        }

        services.TryAddSingleton<IUkraineAlarmRefreshSignal, UkraineAlarmRefreshSignal>();
        services.AddHttpClient(AlertSourceCatalog.UkraineAlarmCode, client =>
        {
            client.BaseAddress = new Uri(AlertSourceCatalog.UkraineAlarmBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            PooledConnectionLifetime = UkraineAlarmConnectionLifetime
        });
        services.AddSingleton(serviceProvider => new UkraineAlarmApiClient(
            serviceProvider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(AlertSourceCatalog.UkraineAlarmCode),
            serviceProvider.GetRequiredService<IOptions<UkraineAlarmOptions>>()));
        services.AddSingleton<IUkraineAlarmWebhookRegistrar>(serviceProvider =>
            serviceProvider.GetRequiredService<UkraineAlarmApiClient>());
        services.AddSingleton<UkraineAlarmProvider>();
        services.AddSingleton<IAlertSourceProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<AlertsInUaProvider>());
        services.AddSingleton<IAlertSourceProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<NeptunProvider>());
        services.AddSingleton<IAlertSourceProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<UkraineAlarmProvider>());
        services.AddSingleton<AlertSourcesStatusService>();
        services.AddSingleton<UkraineAlarmWebhookRegistrationService>();
        if (!string.IsNullOrWhiteSpace(configuration["UkraineAlarm:Token"]))
        {
            services.AddHostedService<UkraineAlarmPollingBackgroundService>();
        }

        services.AddHttpClient(AlertsInUaProvider.ClientName, client =>
        {
            client.BaseAddress = new Uri(AlertSourceCatalog.AlertsInUaBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });
        services.AddHttpClient(NeptunProvider.ClientName, client =>
        {
            client.BaseAddress = new Uri(AlertSourceCatalog.NeptunBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        return services;
    }
}
