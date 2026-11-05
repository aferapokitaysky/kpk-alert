using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Watcher.Api.AlertSources;

public static class NeptunRealtimeServiceCollectionExtensions
{
    public static IServiceCollection AddNeptunRealtimeMonitor(
        this IServiceCollection services,
        NeptunRealtimeOptions? options = null)
    {
        NeptunRealtimeOptions configuredOptions = options ?? new NeptunRealtimeOptions();
        if (configuredOptions.HeartbeatTimeout <= TimeSpan.Zero ||
            configuredOptions.RestFallbackInterval < TimeSpan.FromSeconds(15) ||
            configuredOptions.InitialReconnectDelay <= TimeSpan.Zero ||
            configuredOptions.MaximumReconnectDelay < configuredOptions.InitialReconnectDelay ||
            configuredOptions.MaximumMessageBytes <= 0)
        {
            throw new ArgumentException("Параметри потоку NEPTUN некоректні.", nameof(options));
        }

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<INeptunRealtimeConnectionFactory, NeptunRealtimeConnectionFactory>();
        services.AddSingleton(configuredOptions);
        services.AddHostedService<NeptunRealtimeMonitor>();
        return services;
    }
}
