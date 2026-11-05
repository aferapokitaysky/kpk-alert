using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Watcher.Api.Delivery;

public static class DeviceCommandTransportServiceCollectionExtensions
{
    public static IServiceCollection AddDeviceCommandTransport(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddSingleton<DeviceCommandSessionRegistry>();
        services.AddScoped<DeviceCommandDispatchBatchProcessor>();
        services.AddOptions<DeviceCommandTransportOptions>()
            .Bind(configuration.GetSection(DeviceCommandTransportOptions.SectionName))
            .Validate(options => options.IsValid(),
                "Перевірте інтервали доставки, повторної спроби та розмір пакета команд пристроям.")
            .ValidateOnStart();
        services.AddHostedService<DeviceCommandTransportBackgroundService>();
        return services;
    }
}
