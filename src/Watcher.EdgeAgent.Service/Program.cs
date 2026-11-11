using Microsoft.Extensions.Hosting;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;
using Watcher.EdgeAgent.Infrastructure.Persistence;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.Service;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Services.AddWindowsService(options => options.ServiceName = "KGTA Alert Edge Agent");

        var configurationPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "KGTA", "Alert", "edge-agent.json");
        builder.Configuration.AddJsonFile(configurationPath, optional: false, reloadOnChange: false);
        EdgeAgentOptions options = ReadOptions(builder.Configuration);
        var configurationError = options.Validate();
        if (configurationError is not null)
        {
            throw new InvalidOperationException($"Конфігурація вузла некоректна: {configurationError}.");
        }

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<EdgeAgentDatabaseBackup>();
        builder.Services.AddSingleton<IEdgeAgentClock>(_ => new SystemEdgeAgentClock(TimeProvider.System));
        builder.Services.AddSingleton<SqliteEdgeAgentStore>(_ => new SqliteEdgeAgentStore(
            options.DatabasePath, options.MaximumReceiptQueue));
        builder.Services.AddSingleton<IEdgeAgentStore>(services => services.GetRequiredService<SqliteEdgeAgentStore>());
        builder.Services.AddSingleton<EdgeAgentMaintenanceCoordinator>();
        builder.Services.AddSingleton(_ => new InstallerMaintenanceGate());
        builder.Services.AddSingleton<LocalAudioAssetResolver>(_ => new LocalAudioAssetResolver(options.AudioRootDirectory));
        builder.Services.AddSingleton<IAudioAssetResolver>(services => services.GetRequiredService<LocalAudioAssetResolver>());
        builder.Services.AddSingleton<EdgeAgentStatusProvider>();
        builder.Services.AddSingleton<EdgeAgentStartupCoordinator>();
        builder.Services.AddSingleton<IAudioPlaybackEventHandler, EdgeAgentPlaybackEventHandler>();
        builder.Services.AddSingleton<WasapiAudioPlayer>();
        builder.Services.AddSingleton<IAudioPlayer>(services => services.GetRequiredService<WasapiAudioPlayer>());
        builder.Services.AddSingleton<IAudioEndpointAvailabilityMonitor, WasapiAudioEndpointAvailabilityMonitor>();
        builder.Services.AddSingleton<DeviceCommandProcessor>(services => new DeviceCommandProcessor(
            services.GetRequiredService<IEdgeAgentStore>(),
            services.GetRequiredService<IAudioAssetResolver>(),
            services.GetRequiredService<IAudioPlayer>(),
            services.GetRequiredService<IEdgeAgentClock>(),
            TimeSpan.FromSeconds(options.MaximumClockSkewSeconds), options.MaximumReceiptQueue,
            options.AudioEndpointId,
            services.GetRequiredService<EdgeAgentMaintenanceCoordinator>(),
            services.GetRequiredService<InstallerMaintenanceGate>()));
        builder.Services.AddSingleton<DeviceCommandTransport>(services => new DeviceCommandTransport(
            options,
            services.GetRequiredService<DeviceCommandProcessor>(),
            (code, _) =>
            {
                services.GetRequiredService<EdgeAgentStatusProvider>().ReportDiagnostic(code);
                return ValueTask.CompletedTask;
            },
            identity =>
            {
                services.GetRequiredService<EdgeAgentStatusProvider>().Update(current => current with
                {
                    DeviceId = identity.DeviceId,
                    LocationId = identity.LocationId
                });
                return ValueTask.CompletedTask;
            },
            connectionState =>
            {
                services.GetRequiredService<EdgeAgentStatusProvider>().UpdateTransportState(connectionState);
                return ValueTask.CompletedTask;
            }));
        builder.Services.AddHostedService<EdgeAgentHostedService>();
        builder.Services.AddHostedService<EdgeAgentStatusPipeService>();

        await builder.Build().RunAsync();
    }

    private static EdgeAgentOptions ReadOptions(IConfiguration configuration)
    {
        IConfigurationSection section = configuration.GetSection("EdgeAgent");
        var baseAddress = section["BaseAddress"] ?? throw new InvalidOperationException("Не задано BaseAddress.");
        return new EdgeAgentOptions
        {
            BaseAddress = new Uri(baseAddress, UriKind.Absolute),
            IdentityPath = section["IdentityPath"] ?? "/api/v1/device/identity",
            WebSocketPath = section["WebSocketPath"] ?? "/api/v1/device/commands/stream",
            ReceiptPath = section["ReceiptPath"] ?? "/api/v1/device/command-receipts",
            DatabasePath = section["DatabasePath"] ?? throw new InvalidOperationException("Не задано DatabasePath."),
            AudioRootDirectory = section["AudioRootDirectory"] ?? throw new InvalidOperationException("Не задано AudioRootDirectory."),
            ClientCertificateThumbprint = section["ClientCertificateThumbprint"],
            AudioEndpointId = section["AudioEndpointId"],
            MaximumClockSkewSeconds = section.GetValue("MaximumClockSkewSeconds", 120),
            MaximumWebSocketMessageBytes = section.GetValue("MaximumWebSocketMessageBytes", 32 * 1024),
            MaximumReceiptQueue = section.GetValue("MaximumReceiptQueue", 10_000),
            ReconnectInitialDelaySeconds = section.GetValue("ReconnectInitialDelaySeconds", 1),
            ReconnectMaximumDelaySeconds = section.GetValue("ReconnectMaximumDelaySeconds", 30),
            ReceiptRetryMaximumDelaySeconds = section.GetValue("ReceiptRetryMaximumDelaySeconds", 60)
        };
    }
}
