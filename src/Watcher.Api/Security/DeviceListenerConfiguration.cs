using System.Security.Cryptography.X509Certificates;

namespace Watcher.Api.Security;

public sealed class DeviceListenerCertificates(
    X509Certificate2 serverCertificate,
    X509Certificate2 clientCaCertificate) : IDisposable
{
    public X509Certificate2 ServerCertificate { get; } = serverCertificate;
    public X509Certificate2 ClientCaCertificate { get; } = clientCaCertificate;

    public void Dispose()
    {
        ServerCertificate.Dispose();
        ClientCaCertificate.Dispose();
    }
}

public static class DeviceListenerConfiguration
{
    public static DeviceListenerCertificates? ValidateAndLoad(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var enabled = configuration.GetValue<bool>("DeviceListener:Enabled");
        if (!enabled)
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    "Для production потрібно ввімкнути окремий mTLS listener пристроїв.");
            }

            return null;
        }

        var httpPort = configuration.GetValue<int?>("DeviceListener:HttpPort");
        var devicePort = configuration.GetValue<int?>("DeviceListener:Port");
        var serverCertificatePath = configuration["DeviceListener:ServerCertificatePath"];
        var serverCertificatePasswordPath = configuration["DeviceListener:ServerCertificatePasswordFile"];
        var clientCaCertificatePath = configuration["DeviceListener:ClientCaCertificatePath"];

        if (!IsValidPort(httpPort) || !IsValidPort(devicePort) || httpPort == devicePort)
        {
            throw new InvalidOperationException(
                "mTLS listener має використовувати окремий коректний TCP-порт від HTTP listener.");
        }

        if (string.IsNullOrWhiteSpace(serverCertificatePath) ||
            string.IsNullOrWhiteSpace(serverCertificatePasswordPath) ||
            string.IsNullOrWhiteSpace(clientCaCertificatePath))
        {
            throw new InvalidOperationException(
                "Для mTLS потрібні серверний PFX, файл його пароля та кореневий CA пристроїв.");
        }

        var serverCertificatePassword = File.ReadAllText(serverCertificatePasswordPath)
            .TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(serverCertificatePassword))
        {
            throw new InvalidOperationException("Файл пароля серверного сертифіката mTLS порожній.");
        }

        X509Certificate2 serverCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            serverCertificatePath, serverCertificatePassword);
        X509Certificate2? clientCaCertificate = null;
        try
        {
            if (!serverCertificate.HasPrivateKey)
            {
                throw new InvalidOperationException("Серверний сертифікат mTLS має містити приватний ключ.");
            }

            clientCaCertificate = X509CertificateLoader.LoadCertificateFromFile(clientCaCertificatePath);
            ValidateClientCa(clientCaCertificate);
            return new DeviceListenerCertificates(serverCertificate, clientCaCertificate);
        }
        catch
        {
            serverCertificate.Dispose();
            clientCaCertificate?.Dispose();
            throw;
        }
    }

    private static bool IsValidPort(int? port) => port is >= 1 and <= 65535;

    private static void ValidateClientCa(X509Certificate2 clientCaCertificate)
    {
        X509BasicConstraintsExtension? basicConstraints = clientCaCertificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (basicConstraints?.CertificateAuthority != true ||
            !clientCaCertificate.SubjectName.RawData.AsSpan().SequenceEqual(clientCaCertificate.IssuerName.RawData))
        {
            throw new InvalidOperationException("Сертифікат пристроїв має бути самопідписаним кореневим CA.");
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(clientCaCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (!chain.Build(clientCaCertificate) ||
            !chain.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(clientCaCertificate.RawData))
        {
            throw new InvalidOperationException("Не вдалося перевірити сертифікат кореневого CA пристроїв.");
        }
    }
}
