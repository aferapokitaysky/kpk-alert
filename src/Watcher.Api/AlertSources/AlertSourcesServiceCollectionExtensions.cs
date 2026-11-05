using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public static class AlertSourcesServiceCollectionExtensions
{
    public static IServiceCollection AddAlertSources(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var token = configuration["ALERTS_IN_UA_TOKEN"] ??
            configuration["AlertSources:AlertsInUa:Token"];
        services.AddSingleton<IOptions<AlertsInUaOptions>>(Options.Create(new AlertsInUaOptions(token)));
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<AlertStatusAggregator>();
        services.AddSingleton<AlertsInUaProvider>();
        services.AddSingleton<NeptunProvider>();
        services.AddSingleton<IAlertSourceProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<AlertsInUaProvider>());
        services.AddSingleton<IAlertSourceProvider>(serviceProvider =>
            serviceProvider.GetRequiredService<NeptunProvider>());
        services.AddSingleton<AlertSourcesStatusService>();

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
