using Microsoft.Extensions.DependencyInjection;

namespace Watcher.Api.Scheduling;

public static class MinuteOfSilenceServiceCollectionExtensions
{
    public static IServiceCollection AddMinuteOfSilenceScheduler(this IServiceCollection services)
    {
        services.AddScoped<MinuteOfSilenceOccurrenceProcessor>();
        services.AddHostedService<MinuteOfSilenceBackgroundService>();
        return services;
    }
}
