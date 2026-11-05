using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Server.Kestrel.Https;

namespace Watcher.Api.Security;

public static class DeviceClientCertificateTrust
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";

    public static bool IsTrusted(
        X509Certificate2? certificate,
        X509Certificate2 trustedRoot,
        X509RevocationMode revocationMode = X509RevocationMode.Online)
    {
        if (certificate is null)
        {
            return false;
        }

        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(trustedRoot);
        chain.ChainPolicy.RevocationMode = revocationMode;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
        return chain.Build(certificate);
    }

    public static void ConfigureHttps(
        HttpsConnectionAdapterOptions options,
        X509Certificate2 serverCertificate,
        X509Certificate2 trustedRoot,
        X509RevocationMode revocationMode = X509RevocationMode.Online)
    {
        options.ServerCertificate = serverCertificate;
        options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
        options.ClientCertificateValidation = (certificate, _, _) =>
            IsTrusted(certificate, trustedRoot, revocationMode);
    }
}

public static class DeviceCertificateAuthentication
{
    public const string Scheme = "DeviceCertificate";
    public const string DeviceIdClaim = "kgta_device_id";
    public const string LocationIdClaim = "kgta_location_id";

    public static IServiceCollection AddDeviceCertificateAuthentication(
        this IServiceCollection services,
        X509Certificate2? clientCaCertificate)
    {
        services.AddAuthentication()
            .AddCertificate(Scheme, options =>
            {
                options.AllowedCertificateTypes = CertificateTypes.Chained;
                options.ValidateCertificateUse = true;
                options.ValidateValidityPeriod = true;
                options.RevocationMode = X509RevocationMode.Online;
                options.ChainTrustValidationMode = clientCaCertificate is null
                    ? X509ChainTrustMode.System
                    : X509ChainTrustMode.CustomRootTrust;
                if (clientCaCertificate is not null)
                {
                    options.CustomTrustStore.Add(clientCaCertificate);
                }

                options.Events = new CertificateAuthenticationEvents
                {
                    OnCertificateValidated = async context =>
                    {
                        DeviceCertificateAuthenticator? authenticator = context.HttpContext.RequestServices
                            .GetService<DeviceCertificateAuthenticator>();
                        if (authenticator is null)
                        {
                            context.Fail("Device certificate authentication is unavailable.");
                            return;
                        }

                        DeviceIdentity? device = await authenticator.AuthenticateAsync(
                            context.ClientCertificate,
                            context.HttpContext.RequestAborted);
                        if (device is null)
                        {
                            context.Fail("The device certificate is not registered or has been revoked.");
                            return;
                        }

                        List<Claim> claims =
                        [
                            new(DeviceIdClaim, device.DeviceId.ToString()),
                            new(LocationIdClaim, device.LocationId.ToString())
                        ];
                        context.Principal = new ClaimsPrincipal(
                            new ClaimsIdentity(claims, context.Scheme.Name));
                        context.Success();
                    }
                };
            });

        return services;
    }
}
