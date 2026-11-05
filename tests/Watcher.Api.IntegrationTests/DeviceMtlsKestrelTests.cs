using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watcher.Api.Controllers;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceMtlsKestrelTests
{
    [Fact]
    public async Task Kestrel_accepts_a_registered_certificate_and_rejects_missing_or_untrusted_certificates()
    {
        using TestCertificateAuthority trustedAuthority = new();
        using TestCertificateAuthority untrustedAuthority = new();
        using X509Certificate2 registeredCertificate = trustedAuthority.IssueClientCertificate("registered-device");
        using X509Certificate2 untrustedCertificate = untrustedAuthority.IssueClientCertificate("untrusted-device");
        using X509Certificate2 serverCertificate = CreateServerCertificate();
        var deviceId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await using WebApplication app = await StartKestrelAsync(
            serverCertificate, trustedAuthority.Root, deviceId, locationId, registeredCertificate);
        Uri address = GetHttpsAddress(app);

        using HttpClient validClient = CreateClient(address, registeredCertificate);
        using HttpResponseMessage identityResponse = await validClient.GetAsync("/api/v1/device/identity");
        Assert.Equal(HttpStatusCode.OK, identityResponse.StatusCode);
        using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());

        Assert.Equal(deviceId, identity.RootElement.GetProperty("deviceId").GetGuid());
        Assert.Equal(locationId, identity.RootElement.GetProperty("locationId").GetGuid());

        using HttpClient missingCertificateClient = CreateClient(address, null);
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => missingCertificateClient.GetAsync("/api/v1/device/identity"));

        using HttpClient untrustedClient = CreateClient(address, untrustedCertificate);
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => untrustedClient.GetAsync("/api/v1/device/identity"));
    }

    private static async Task<WebApplication> StartKestrelAsync(
        X509Certificate2 serverCertificate,
        X509Certificate2 trustedRoot,
        Guid deviceId,
        Guid locationId,
        X509Certificate2 registeredCertificate)
    {
        var databaseName = Guid.NewGuid().ToString();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, 0, listenOptions => listenOptions.UseHttps(httpsOptions =>
                DeviceClientCertificateTrust.ConfigureHttps(
                    httpsOptions, serverCertificate, trustedRoot, X509RevocationMode.NoCheck))));
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(DeviceIdentityController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddDeviceCertificateAuthentication(trustedRoot);
        builder.Services.PostConfigure<CertificateAuthenticationOptions>(DeviceCertificateAuthentication.Scheme,
            options => options.RevocationMode = X509RevocationMode.NoCheck);
        builder.Services.AddDbContext<WatcherDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<DeviceCertificateAuthenticator>();

        WebApplication app = builder.Build();
        using (IServiceScope scope = app.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            db.Locations.Add(new Location
            {
                Id = locationId,
                Code = $"kestrel-test-{locationId:N}",
                Name = "Тестовий корпус",
                IsActive = true,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            db.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = locationId,
                Name = "Тестовий вузол",
                CertificateThumbprint = Convert.ToHexString(SHA256.HashData(registeredCertificate.RawData))
            });
            await db.SaveChangesAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        return app;
    }

    private static Uri GetHttpsAddress(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new UriBuilder(address) { Scheme = Uri.UriSchemeHttps }.Uri;
    }

    private static HttpClient CreateClient(Uri address, X509Certificate2? clientCertificate)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            ClientCertificateOptions = ClientCertificateOption.Manual
        };
        if (clientCertificate is not null)
        {
            handler.ClientCertificates.Add(clientCertificate);
        }

        return new HttpClient(handler) { BaseAddress = address };
    }

    private static X509Certificate2 CreateServerCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        var enhancedKeyUsage = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsage, true));
        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }
}
