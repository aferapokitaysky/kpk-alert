using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Infrastructure.Delivery;

namespace Watcher.Api.Delivery;

public static class CommandOutboxServiceCollectionExtensions
{
    public static IServiceCollection AddCommandOutboxWorker(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddScoped<CommandDeliveryMaterializer>();
        services.AddScoped<CommandOutboxBatchProcessor>();
        services.AddOptions<CommandOutboxWorkerOptions>()
            .Bind(configuration.GetSection(CommandOutboxWorkerOptions.SectionName))
            .Validate(options => options.IsValid(),
                "Interval має бути від 100 мс до 5 хвилин, а розмір пакета — від 1 до 500.")
            .ValidateOnStart();
        services.AddHostedService<CommandOutboxBackgroundService>();
        return services;
    }
}
