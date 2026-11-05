using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Watcher.Api.Security;

namespace Watcher.UnitTests;

public sealed class DeviceListenerConfigurationTests
{
    [Fact]
    public void Production_requires_a_trusted_proxy_and_public_https_port()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TrustedProxyConfiguration.AddTrustedProxyForwarding(
                new ServiceCollection(), configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("reverse proxy", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Trusted_proxy_configuration_sets_the_public_port_and_forwarding_allowlist()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:HttpsPort"] = "443",
                ["ReverseProxy:KnownProxies:0"] = "10.20.0.10"
            })
            .Build();
        ServiceCollection services = new();

        TrustedProxyConfiguration.AddTrustedProxyForwarding(
            services, configuration, new TestHostEnvironment(Environments.Production));

        using ServiceProvider provider = services.BuildServiceProvider();
        ForwardedHeadersOptions forwarding = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        HttpsRedirectionOptions redirection = provider.GetRequiredService<IOptions<HttpsRedirectionOptions>>().Value;
        Assert.Equal(ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto |
                     ForwardedHeaders.XForwardedHost, forwarding.ForwardedHeaders);
        Assert.Equal(1, forwarding.ForwardLimit);
        Assert.Contains(IPAddress.Parse("10.20.0.10"), forwarding.KnownProxies);
        Assert.DoesNotContain(IPAddress.Loopback, forwarding.KnownProxies);
        Assert.DoesNotContain(IPAddress.IPv6Loopback, forwarding.KnownProxies);
        Assert.Empty(forwarding.KnownIPNetworks);
        Assert.Equal(443, redirection.HttpsPort);
    }

    [Fact]
    public void Trusted_proxy_configuration_rejects_a_hostname_instead_of_an_ip_address()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:HttpsPort"] = "443",
                ["ReverseProxy:KnownProxies:0"] = "proxy.internal"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TrustedProxyConfiguration.AddTrustedProxyForwarding(
                new ServiceCollection(), configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("IP-адресою", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Public_https_port_cannot_be_the_device_mtls_port()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:HttpsPort"] = "8443",
                ["ReverseProxy:KnownProxies:0"] = "10.20.0.10",
                ["DeviceListener:Port"] = "8443"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => TrustedProxyConfiguration.AddTrustedProxyForwarding(
                new ServiceCollection(), configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("mTLS-порт", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Forwarded_headers_from_an_unlisted_loopback_address_are_ignored()
    {
        using ServiceProvider provider = CreateForwardedHeadersProvider();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await RunForwardedHeadersAsync(provider, context);

        Assert.Equal("http", context.Request.Scheme);
    }

    [Fact]
    public async Task Forwarded_headers_from_an_allowlisted_proxy_update_the_request_scheme()
    {
        using ServiceProvider provider = CreateForwardedHeadersProvider();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.20.0.10");
        context.Request.Headers["X-Forwarded-Proto"] = "https";

        await RunForwardedHeadersAsync(provider, context);

        Assert.Equal("https", context.Request.Scheme);
    }

    [Fact]
    public void Production_requires_the_device_listener_to_be_enabled()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DeviceListenerConfiguration.ValidateAndLoad(
                configuration, new TestHostEnvironment(Environments.Production)));

        Assert.Contains("mTLS listener", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Device_listener_requires_a_separate_port()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DeviceListener:Enabled"] = "true",
                ["DeviceListener:HttpPort"] = "8443",
                ["DeviceListener:Port"] = "8443"
            })
            .Build();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => DeviceListenerConfiguration.ValidateAndLoad(
                configuration, new TestHostEnvironment(Environments.Development)));

        Assert.Contains("окремий", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Valid_device_listener_configuration_is_accepted()
    {
        var serverCertificatePath = Path.GetTempFileName();
        var serverCertificatePasswordPath = Path.GetTempFileName();
        var clientCaCertificatePath = Path.GetTempFileName();
        const string serverCertificatePassword = "test-only-password";
        try
        {
            using X509Certificate2 serverCertificate = CreateCertificate("CN=localhost", isCertificateAuthority: false);
            using X509Certificate2 clientCaCertificate = CreateCertificate("CN=KGTA test CA", isCertificateAuthority: true);
            File.WriteAllBytes(serverCertificatePath, serverCertificate.Export(
                X509ContentType.Pkcs12, serverCertificatePassword));
            File.WriteAllText(serverCertificatePasswordPath, serverCertificatePassword + Environment.NewLine);
            File.WriteAllText(clientCaCertificatePath, clientCaCertificate.ExportCertificatePem());

            IConfiguration configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DeviceListener:Enabled"] = "true",
                    ["DeviceListener:HttpPort"] = "8080",
                    ["DeviceListener:Port"] = "8443",
                    ["DeviceListener:ClientCaCertificatePath"] = clientCaCertificatePath,
                    ["DeviceListener:ServerCertificatePath"] = serverCertificatePath,
                    ["DeviceListener:ServerCertificatePasswordFile"] = serverCertificatePasswordPath
                })
                .Build();

            using DeviceListenerCertificates? certificates = DeviceListenerConfiguration.ValidateAndLoad(
                configuration, new TestHostEnvironment(Environments.Production));
            Assert.NotNull(certificates);
            Assert.True(certificates.ServerCertificate.HasPrivateKey);
            Assert.Equal(clientCaCertificate.Thumbprint, certificates.ClientCaCertificate.Thumbprint);
        }
        finally
        {
            File.Delete(serverCertificatePath);
            File.Delete(serverCertificatePasswordPath);
            File.Delete(clientCaCertificatePath);
        }
    }

    private static X509Certificate2 CreateCertificate(string subject, bool isCertificateAuthority)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(isCertificateAuthority, false, 0, true));
        if (isCertificateAuthority)
        {
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        }

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static ServiceProvider CreateForwardedHeadersProvider()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReverseProxy:HttpsPort"] = "443",
                ["ReverseProxy:KnownProxies:0"] = "10.20.0.10"
            })
            .Build();
        ServiceCollection services = new();
        services.AddLogging();
        TrustedProxyConfiguration.AddTrustedProxyForwarding(
            services, configuration, new TestHostEnvironment(Environments.Development));
        return services.BuildServiceProvider();
    }

    private static Task RunForwardedHeadersAsync(ServiceProvider provider, DefaultHttpContext context)
    {
        ApplicationBuilder application = new(provider);
        application.UseForwardedHeaders();
        application.Run(_ => Task.CompletedTask);
        return application.Build()(context);
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Watcher.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
