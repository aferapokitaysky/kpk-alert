using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watcher.Api.Controllers;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceMtlsKestrelTests
{
    [Fact]
    public async Task Command_stream_is_available_only_on_the_mtls_listener()
    {
        using TestCertificateAuthority trustedAuthority = new();
        using TestKeyCertificate clientCertificate = CreateClientCertificate(trustedAuthority, "stream-device");
        using TestKeyCertificate serverCertificate = CreateServerCertificate();
        var deviceId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        (WebApplication app, Uri webAddress, Uri deviceAddress) = await StartStreamKestrelAsync(
            serverCertificate.Certificate, trustedAuthority.Root, deviceId, locationId, clientCertificate.Certificate);
        await using (app)
        {
            using HttpClient missingCertificateClient = CreateClient(webAddress, null);
            using HttpResponseMessage unauthenticatedResponse = await missingCertificateClient.GetAsync(
                "/api/v1/device/commands/stream");
            Assert.Equal(HttpStatusCode.Forbidden, unauthenticatedResponse.StatusCode);

            using HttpClient validWebClient = CreateClient(webAddress, clientCertificate.Certificate);
            using HttpResponseMessage wrongListenerResponse = await validWebClient.GetAsync(
                "/api/v1/device/commands/stream");
            Assert.Equal(HttpStatusCode.NotFound, wrongListenerResponse.StatusCode);

            using ClientWebSocket missingCertificateSocket = CreateWebSocketClient(null);
            Uri deviceStreamAddress = new UriBuilder(deviceAddress)
            {
                Scheme = "wss",
                Path = "/api/v1/device/commands/stream"
            }.Uri;
            await Assert.ThrowsAnyAsync<WebSocketException>(
                () => missingCertificateSocket.ConnectAsync(deviceStreamAddress, CancellationToken.None));

            using ClientWebSocket validSocket = CreateWebSocketClient(clientCertificate.Certificate);
            await validSocket.ConnectAsync(deviceStreamAddress, CancellationToken.None);
            Assert.Equal(WebSocketState.Open, validSocket.State);

            using IServiceScope scope = app.Services.CreateScope();
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            DeviceSession? session = null;
            for (var attempt = 0; attempt < 350; attempt++)
            {
                session = await db.DeviceSessions.AsNoTracking().SingleOrDefaultAsync(
                    item => item.DeviceId == deviceId);
                if (session is not null && session.LastSeenAtUtc > session.ConnectedAtUtc)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            Assert.NotNull(session);
            Assert.True(session.LastSeenAtUtc > session.ConnectedAtUtc);
            await validSocket.CloseOutputAsync(
                WebSocketCloseStatus.NormalClosure, "Тест завершено", CancellationToken.None);

            for (var attempt = 0; attempt < 100; attempt++)
            {
                session = await db.DeviceSessions.AsNoTracking().SingleOrDefaultAsync(
                    item => item.DeviceId == deviceId);
                if (session?.DisconnectedAtUtc is not null)
                {
                    break;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            Assert.NotNull(session?.DisconnectedAtUtc);
        }
    }

    [Fact]
    public async Task Kestrel_accepts_a_registered_certificate_and_rejects_missing_or_untrusted_certificates()
    {
        using TestCertificateAuthority trustedAuthority = new();
        using TestCertificateAuthority untrustedAuthority = new();
        using TestKeyCertificate registeredCertificate = CreateClientCertificate(trustedAuthority, "registered-device");
        using TestKeyCertificate untrustedCertificate = CreateClientCertificate(untrustedAuthority, "untrusted-device");
        using TestKeyCertificate serverCertificate = CreateServerCertificate();
        var deviceId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await using WebApplication app = await StartKestrelAsync(
            serverCertificate.Certificate, trustedAuthority.Root, deviceId, locationId, registeredCertificate.Certificate);
        Uri address = GetHttpsAddress(app);

        using HttpClient validClient = CreateClient(address, registeredCertificate.Certificate);
        using HttpResponseMessage identityResponse = await validClient.GetAsync("/api/v1/device/identity");
        Assert.Equal(HttpStatusCode.OK, identityResponse.StatusCode);
        using var identity = JsonDocument.Parse(await identityResponse.Content.ReadAsStringAsync());

        Assert.Equal(deviceId, identity.RootElement.GetProperty("deviceId").GetGuid());
        Assert.Equal(locationId, identity.RootElement.GetProperty("locationId").GetGuid());

        using HttpClient missingCertificateClient = CreateClient(address, null);
        await Assert.ThrowsAnyAsync<HttpRequestException>(
            () => missingCertificateClient.GetAsync("/api/v1/device/identity"));

        using HttpClient untrustedClient = CreateClient(address, untrustedCertificate.Certificate);
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

    private static async Task<(WebApplication App, Uri WebAddress, Uri DeviceAddress)> StartStreamKestrelAsync(
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
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DeviceListener:Port"] = "0"
        });
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Any, 0, listenOptions =>
                listenOptions.UseHttps(httpsOptions =>
                {
                    httpsOptions.ServerCertificate = serverCertificate;
                    httpsOptions.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
                    httpsOptions.ClientCertificateValidation = (certificate, _, _) =>
                        DeviceClientCertificateTrust.IsTrusted(
                            certificate, trustedRoot, X509RevocationMode.NoCheck);
                }));
            options.Listen(IPAddress.Loopback, 0, listenOptions =>
                listenOptions.UseHttps(httpsOptions => DeviceClientCertificateTrust.ConfigureHttps(
                    httpsOptions, serverCertificate, trustedRoot, X509RevocationMode.NoCheck)));
        });
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(DeviceCommandStreamController).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<DeviceCommandSessionRegistry>();
        builder.Services.AddSingleton<DeviceSessionPresenceService>();
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
                Code = $"stream-test-{locationId:N}",
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

        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        Uri webAddress = GetHttpsAddress(app, IPAddress.Any);
        Uri deviceAddress = GetHttpsAddress(app, IPAddress.Loopback);
        builder.Configuration["DeviceListener:Port"] = deviceAddress.Port.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        return (app, webAddress, deviceAddress);
    }

    private static Uri GetHttpsAddress(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new UriBuilder(address) { Scheme = Uri.UriSchemeHttps }.Uri;
    }

    private static Uri GetHttpsAddress(WebApplication app, IPAddress expectedAddress)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses
            .Single(value => IPAddress.TryParse(new Uri(value).Host, out IPAddress? parsed) &&
                parsed.Equals(expectedAddress));
        UriBuilder uri = new(address) { Scheme = Uri.UriSchemeHttps };
        if (expectedAddress.Equals(IPAddress.Any))
        {
            uri.Host = "localhost";
        }

        return uri.Uri;
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

    private static ClientWebSocket CreateWebSocketClient(X509Certificate2? clientCertificate)
    {
        ClientWebSocket socket = new();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        if (clientCertificate is not null)
        {
            socket.Options.ClientCertificates.Add(clientCertificate);
        }

        return socket;
    }

    private static TestKeyCertificate CreateServerCertificate()
    {
        var testKey = CreateTestRsaKey();
        RSA key = testKey.Key;
        string? keyName = testKey.KeyName;
        try
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var enhancedKeyUsage = new OidCollection { new("1.3.6.1.5.5.7.3.1") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsage, true));
            var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
            subjectAlternativeNames.AddDnsName("localhost");
            request.CertificateExtensions.Add(subjectAlternativeNames.Build());
            X509Certificate2 certificate = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            return new TestKeyCertificate(certificate, key, keyName);
        }
        catch
        {
            key.Dispose();
            DeleteTestRsaKey(keyName);
            throw;
        }
    }

    private static TestKeyCertificate CreateClientCertificate(
        TestCertificateAuthority authority,
        string commonName)
    {
        var testKey = CreateTestRsaKey();
        RSA clientKey = testKey.Key;
        string? keyName = testKey.KeyName;
        try
        {
            var request = new CertificateRequest(
                $"CN={commonName}", clientKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            var enhancedKeyUsage = new OidCollection { new("1.3.6.1.5.5.7.3.2") };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(enhancedKeyUsage, true));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            using X509Certificate2 issued = request.Create(
                authority.Root,
                DateTimeOffset.UtcNow.AddMinutes(-1),
                DateTimeOffset.UtcNow.AddDays(7),
                RandomNumberGenerator.GetBytes(16));
            return new TestKeyCertificate(issued.CopyWithPrivateKey(clientKey), clientKey, keyName);
        }
        catch
        {
            clientKey.Dispose();
            DeleteTestRsaKey(keyName);
            throw;
        }
    }

    private static (RSA Key, string? KeyName) CreateTestRsaKey()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (RSA.Create(2048), null);
        }

        var keyName = $"KgtaAlertIntegrationTest-{Guid.NewGuid():N}";
        var keyParameters = new CngKeyCreationParameters
        {
            KeyUsage = CngKeyUsages.Signing,
            Provider = CngProvider.MicrosoftSoftwareKeyStorageProvider
        };
        return (new RSACng(CngKey.Create(CngAlgorithm.Rsa, keyName, keyParameters)), keyName);
    }

    private static void DeleteTestRsaKey(string? keyName)
    {
        if (keyName is null || !OperatingSystem.IsWindows() ||
            !CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider))
        {
            return;
        }

        using var cngKey = CngKey.Open(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider);
        cngKey.Delete();
    }

    private sealed class TestKeyCertificate(
        X509Certificate2 certificate,
        RSA privateKey,
        string? keyName) : IDisposable
    {
        public X509Certificate2 Certificate { get; } = certificate;

        public void Dispose()
        {
            Certificate.Dispose();
            privateKey.Dispose();
            DeleteTestRsaKey(keyName);
        }
    }
}
