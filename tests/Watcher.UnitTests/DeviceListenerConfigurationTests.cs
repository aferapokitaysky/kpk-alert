using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Watcher.Api.Security;

namespace Watcher.UnitTests;

public sealed class DeviceListenerConfigurationTests
{
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

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Watcher.UnitTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
