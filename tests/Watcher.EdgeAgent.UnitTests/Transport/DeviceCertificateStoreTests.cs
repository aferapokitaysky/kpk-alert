using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.UnitTests.Transport;

public sealed class DeviceCertificateStoreTests
{
    [Fact]
    public void FindUsableCertificate_SkipsCertificatesWithoutPrivateKeysAndExpiredCertificates()
    {
        using X509Certificate2 publicOnlyCertificate = CreateCertificate(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5), includePrivateKey: false);
        using X509Certificate2 expiredCertificate = CreateCertificate(
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-5), includePrivateKey: true);
        using X509Certificate2 validCertificate = CreateCertificate(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5), includePrivateKey: true);
        X509Certificate2Collection certificates = [publicOnlyCertificate, expiredCertificate, validCertificate];

        X509Certificate2? selectedCertificate = DeviceCertificateStore.FindUsableCertificate(certificates);

        Assert.Same(validCertificate, selectedCertificate);
    }

    [Fact]
    public void FindUsableCertificate_ReturnsNullWhenNoCertificateHasAnActivePrivateKey()
    {
        using X509Certificate2 expiredCertificate = CreateCertificate(
            DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddMinutes(-5), includePrivateKey: true);
        using X509Certificate2 futureCertificate = CreateCertificate(
            DateTimeOffset.UtcNow.AddMinutes(5), DateTimeOffset.UtcNow.AddMinutes(10), includePrivateKey: true);
        X509Certificate2Collection certificates = [expiredCertificate, futureCertificate];

        Assert.Null(DeviceCertificateStore.FindUsableCertificate(certificates));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void FindClientCertificate_RejectsBlankThumbprint(string thumbprint)
    {
        Assert.Throws<ArgumentException>(() => DeviceCertificateStore.FindClientCertificate(thumbprint));
    }

    [Fact]
    public void FindClientCertificate_UnknownThumbprintFailsClosedWithoutCertificateFixture()
    {
        EdgeAgentTransportException exception = Assert.Throws<EdgeAgentTransportException>(
            () => DeviceCertificateStore.FindClientCertificate("0000000000000000000000000000000000000000"));

        Assert.Equal("device_certificate_unavailable", exception.DiagnosticCode);
    }

    [Fact]
    public void FindClientCertificate_NonHexThumbprintNormalizesToNoMatch()
    {
        EdgeAgentTransportException exception = Assert.Throws<EdgeAgentTransportException>(
            () => DeviceCertificateStore.FindClientCertificate("not-a-certificate"));

        Assert.Equal("device_certificate_unavailable", exception.DiagnosticCode);
    }

    private static X509Certificate2 CreateCertificate(
        DateTimeOffset notBefore,
        DateTimeOffset notAfter,
        bool includePrivateKey)
    {
        using var key = RSA.Create(2048);
        CertificateRequest request = new("CN=Edge Agent Certificate Selection Test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        if (!includePrivateKey)
        {
            using X509Certificate2 publicCertificate = request.CreateSelfSigned(notBefore, notAfter);
            return X509CertificateLoader.LoadCertificate(publicCertificate.Export(X509ContentType.Cert));
        }

        return request.CreateSelfSigned(notBefore, notAfter);
    }
}

public sealed class MtlsHttpClientFactoryTests
{
    [Fact]
    public void Create_SetsBaseAddressAndDisablesGlobalTimeout()
    {
        EdgeAgentOptions options = new()
        {
            BaseAddress = new Uri("https://watcher.example.test"),
            IdentityPath = "/api/v1/device/identity",
            WebSocketPath = "/api/v1/device/commands/stream",
            ReceiptPath = "/api/v1/device/command-receipts",
            DatabasePath = "unused.db",
            AudioRootDirectory = "unused-audio"
        };
        using X509Certificate2 certificate = CreateEphemeralClientCertificate();

        using HttpClient client = MtlsHttpClientFactory.Create(options, certificate);

        Assert.Equal(options.BaseAddress, client.BaseAddress);
        Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout);
    }

    [Fact]
    public void Create_RejectsNullOptions()
    {
        using X509Certificate2 certificate = CreateEphemeralClientCertificate();

        Assert.Throws<ArgumentNullException>(() => MtlsHttpClientFactory.Create(null!, certificate));
    }

    [Fact]
    public void Create_RejectsNullCertificate()
    {
        EdgeAgentOptions options = new()
        {
            BaseAddress = new Uri("https://watcher.example.test"),
            IdentityPath = "/api/v1/device/identity",
            WebSocketPath = "/api/v1/device/commands/stream",
            ReceiptPath = "/api/v1/device/command-receipts",
            DatabasePath = "unused.db",
            AudioRootDirectory = "unused-audio"
        };

        Assert.Throws<ArgumentNullException>(() => MtlsHttpClientFactory.Create(options, null!));
    }

    private static X509Certificate2 CreateEphemeralClientCertificate()
    {
        using var key = RSA.Create(2048);
        CertificateRequest request = new("CN=Edge Agent Test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }
}
