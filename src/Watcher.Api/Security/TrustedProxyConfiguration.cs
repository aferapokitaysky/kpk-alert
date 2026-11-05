using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;

namespace Watcher.Api.Security;

public static class TrustedProxyConfiguration
{
    public static void AddTrustedProxyForwarding(
        IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var proxyAddresses = configuration.GetSection("ReverseProxy:KnownProxies")
            .GetChildren()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var httpsPort = configuration.GetValue<int?>("ReverseProxy:HttpsPort");
        var devicePort = configuration.GetValue<int?>("DeviceListener:Port");

        if (environment.IsProduction() && (proxyAddresses.Length == 0 || httpsPort is not (>= 1 and <= 65535)))
        {
            throw new InvalidOperationException(
                "Для production потрібно налаштувати довірені адреси reverse proxy та публічний HTTPS-порт.");
        }

        if (httpsPort is not null and not (>= 1 and <= 65535))
        {
            throw new InvalidOperationException("Публічний HTTPS-порт reverse proxy має бути в межах 1–65535.");
        }

        if (httpsPort is not null && httpsPort == devicePort)
        {
            throw new InvalidOperationException(
                "Публічний HTTPS-порт reverse proxy не може збігатися з mTLS-портом пристроїв.");
        }

        List<IPAddress> knownProxies = [];
        foreach (var address in proxyAddresses)
        {
            if (!IPAddress.TryParse(address, out IPAddress? parsedAddress))
            {
                throw new InvalidOperationException("Адреса довіреного reverse proxy має бути коректною IP-адресою.");
            }

            knownProxies.Add(parsedAddress);
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                       ForwardedHeaders.XForwardedProto |
                                       ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (IPAddress proxyAddress in knownProxies)
            {
                options.KnownProxies.Add(proxyAddress);
            }
        });

        if (httpsPort is not null)
        {
            services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = httpsPort.Value);
        }
    }
}
