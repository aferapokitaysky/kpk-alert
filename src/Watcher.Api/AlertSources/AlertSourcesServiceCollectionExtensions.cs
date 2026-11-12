using System.Net.Http.Headers;
using System.Text.Json;
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
        services.TryAddSingleton<IAlertSourceChangeSignal, AlertSourceChangeSignal>();
        services.AddSingleton<AlertStatusAggregator>();
        services.AddSingleton<AutomaticAlertDispatchPolicy>();
        IConfigurationSection automaticDispatchSection = configuration
            .GetSection(AutomaticAlertDispatchOptions.SectionName);
        AutomaticAlertDispatchOptions automaticDispatchOptions = automaticDispatchSection
            .Get<AutomaticAlertDispatchOptions>() ?? new AutomaticAlertDispatchOptions();
        BindAlertLevelPresetsJson(automaticDispatchSection, automaticDispatchOptions);
        if (automaticDispatchOptions.PollInterval < TimeSpan.FromSeconds(15))
        {
            throw new InvalidOperationException("Інтервал оцінювання автоматичного оповіщення має бути не меншим за 15 секунд.");
        }

        if (automaticDispatchOptions.StartCommandValiditySeconds is < 15 or > 300)
        {
            throw new InvalidOperationException("Строк дії автоматичної команди має бути від 15 до 300 секунд.");
        }

        if (automaticDispatchOptions.AlertLevelPresets.Any(pair =>
                pair.Key is not ("red" or "yellow") ||
                pair.Value is not { Length: > 0 and <= 80 } ||
                pair.Value.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))))
        {
            throw new InvalidOperationException("Налаштування пресетів рівня тривоги некоректне.");
        }

        if (automaticDispatchOptions.Enabled &&
            (!automaticDispatchOptions.AlertLevelPresets.ContainsKey("red") ||
             !automaticDispatchOptions.AlertLevelPresets.ContainsKey("yellow")))
        {
            throw new InvalidOperationException(
                "Для автоматичного оповіщення налаштуйте окремі опубліковані пресети рівнів red і yellow.");
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

    private static void BindAlertLevelPresetsJson(
        IConfigurationSection section,
        AutomaticAlertDispatchOptions options)
    {
        var json = section["AlertLevelPresetsJson"];
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        if (section.GetSection("AlertLevelPresets").GetChildren().Any())
        {
            throw new InvalidOperationException(
                "Задайте пресети рівнів або через AlertLevelPresetsJson, або через вкладені параметри, але не обома способами.");
        }

        try
        {
            Dictionary<string, string>? presets = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (presets is null)
            {
                throw new JsonException("JSON має містити об'єкт пресетів.");
            }

            options.AlertLevelPresets = new Dictionary<string, string>(presets, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException(
                "AlertLevelPresetsJson має бути JSON-об'єктом із кодами рівнів та пресетів.",
                exception);
        }
    }
}
