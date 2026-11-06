using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.Service;

internal static class DevicePrivateKeyAccessValidator
{
    public static void Validate(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        var challenge = RandomNumberGenerator.GetBytes(32);

        try
        {
            using RSA? rsa = certificate.GetRSAPrivateKey();
            if (rsa is not null)
            {
                _ = rsa.SignHash(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                return;
            }

            using ECDsa? ecdsa = certificate.GetECDsaPrivateKey();
            if (ecdsa is not null)
            {
                _ = ecdsa.SignHash(challenge);
                return;
            }
        }
        catch (CryptographicException exception)
        {
            throw new EdgeAgentTransportException("device_certificate_unavailable", exception);
        }

        throw new EdgeAgentTransportException("device_certificate_unavailable");
    }
}
