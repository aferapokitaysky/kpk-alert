using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Transport;

public static class DeviceCertificateStore
{
    public static X509Certificate2 FindClientCertificate(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        var normalizedThumbprint = NormalizeThumbprint(thumbprint);
        X509Certificate2Collection? matches = null;
        X509Certificate2? certificate = null;
        try
        {
            using X509Store store = new(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            matches = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                normalizedThumbprint,
                validOnly: false);

            certificate = matches
                .Cast<X509Certificate2>()
                .FirstOrDefault(item => item.HasPrivateKey && IsCurrentlyValid(item));

            return certificate ?? throw new EdgeAgentTransportException("device_certificate_unavailable");
        }
        catch (EdgeAgentTransportException)
        {
            throw;
        }
        catch (Exception exception) when (IsCertificateStoreFailure(exception))
        {
            throw new EdgeAgentTransportException("device_certificate_unavailable", exception);
        }
        finally
        {
            if (matches is not null)
            {
                foreach (X509Certificate2 candidate in matches)
                {
                    if (!ReferenceEquals(candidate, certificate))
                    {
                        candidate.Dispose();
                    }
                }
            }
        }
    }

    private static bool IsCertificateStoreFailure(Exception exception) =>
        exception is CryptographicException or IOException or UnauthorizedAccessException or
            SecurityException or PlatformNotSupportedException or ArgumentException;

    private static string NormalizeThumbprint(string value) =>
        new string(value.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private static bool IsCurrentlyValid(X509Certificate2 certificate)
    {
        DateTime nowUtc = DateTime.UtcNow;
        return nowUtc >= certificate.NotBefore.ToUniversalTime() &&
               nowUtc <= certificate.NotAfter.ToUniversalTime();
    }
}

public sealed class EdgeAgentTransportException(string diagnosticCode, Exception? innerException = null)
    : Exception(diagnosticCode, innerException)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}
