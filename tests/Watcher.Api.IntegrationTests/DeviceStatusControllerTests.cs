using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceStatusControllerTests : IClassFixture<PostgresApiFixture>
{
    private const string Path = "/api/v1/admin/devices";
    private readonly PostgresApiFixture _fixture;

    public DeviceStatusControllerTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Open_session_rejects_device_revoked_after_certificate_authentication()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var deviceId = Guid.NewGuid();
        using X509Certificate2 certificate = CreateDeviceCertificate();
        await using WatcherDbContext db = _fixture.CreateDbContext();
        Device device = new()
        {
            Id = deviceId,
            LocationId = seed.FirstLocationId,
            Name = "Відкликаний вузол",
            CertificateThumbprint = GetFingerprint(certificate)
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        DeviceCertificateAuthenticator authenticator = new(db, TimeProvider.System);
        Assert.NotNull(await authenticator.AuthenticateAsync(certificate, CancellationToken.None));

        device.RevokedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        await Assert.ThrowsAnyAsync<DeviceSessionUnavailableException>(() =>
            _fixture.DevicePresence.OpenSessionAsync(deviceId, CancellationToken.None));
        Assert.Empty(await db.DeviceSessions.Where(session => session.DeviceId == deviceId).ToArrayAsync());
    }

    [PostgresFact]
    public async Task Registration_rejects_session_after_location_is_deactivated()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var deviceId = Guid.NewGuid();
        await using WatcherDbContext db = _fixture.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            LocationId = seed.FirstLocationId,
            Name = "Вузол неактивного корпусу",
            CertificateThumbprint = new string('a', 64)
        });
        await db.SaveChangesAsync();

        Guid sessionId = await _fixture.DevicePresence.OpenSessionAsync(deviceId, CancellationToken.None);
        Location location = await db.Locations.SingleAsync(item => item.Id == seed.FirstLocationId);
        location.IsActive = false;
        await db.SaveChangesAsync();

        Assert.False(await _fixture.DevicePresence.IsSessionActiveAsync(sessionId, CancellationToken.None));
    }

    [PostgresFact]
    public async Task Concurrent_reconnects_leave_only_the_last_database_session_active()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var deviceId = Guid.NewGuid();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = seed.FirstLocationId,
                Name = "Вузол reconnect",
                CertificateThumbprint = new string('a', 64)
            });
            await db.SaveChangesAsync();
        }

        Guid[] sessionIds = await Task.WhenAll(
            _fixture.DevicePresence.OpenSessionAsync(deviceId, CancellationToken.None),
            _fixture.DevicePresence.OpenSessionAsync(deviceId, CancellationToken.None));

        using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        DeviceSession[] sessions = await verifyDb.DeviceSessions
            .Where(session => session.DeviceId == deviceId)
            .ToArrayAsync();
        DeviceSession activeSession = Assert.Single(sessions, session => session.DisconnectedAtUtc == null);
        Assert.Contains(activeSession.Id, sessionIds);
        Assert.Equal(2, sessions.Length);
    }

    [PostgresFact]
    public async Task Device_status_is_restricted_no_store_and_does_not_expose_certificate_data()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var auditorId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var lastSeenAtUtc = DateTimeOffset.Parse("2026-11-06T08:05:00Z");
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Role auditorRole = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Аудитор" };
            db.Users.Add(new UserAccount
            {
                Id = auditorId,
                ExternalSubject = "auditor",
                Email = "auditor@example.test"
            });
            db.Roles.Add(auditorRole);
            db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = auditorId, RoleId = auditorRole.Id });
            db.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = seed.FirstLocationId,
                Name = "Вузол 01",
                CertificateThumbprint = new string('f', 64)
            });
            db.DeviceSessions.Add(new DeviceSession
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                ConnectedAtUtc = lastSeenAtUtc.AddMinutes(-4),
                DisconnectedAtUtc = lastSeenAtUtc.AddMinutes(-1),
                LastSeenAtUtc = lastSeenAtUtc
            });
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage administrator = await SendGetAsync(seed.AdministratorId, "admin", "?page=1&pageSize=1");
        using HttpResponseMessage auditor = await SendGetAsync(auditorId, "auditor");
        using HttpResponseMessage operatorResponse = await SendGetAsync(seed.OperatorId, "notificator");
        using HttpResponseMessage anonymous = await _fixture.Client.GetAsync(Path);

        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            db.DeviceSessions.Add(new DeviceSession
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                ConnectedAtUtc = now,
                LastSeenAtUtc = now
            });
            await db.SaveChangesAsync();
        }
        using HttpResponseMessage connectedResponse = await SendGetAsync(seed.AdministratorId, "admin");

        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
        Assert.Equal("no-store", administrator.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await administrator.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(deviceId, item.GetProperty("deviceId").GetGuid());
        Assert.Equal("Вузол 01", item.GetProperty("name").GetString());
        Assert.Equal("Північний корпус", item.GetProperty("locationName").GetString());
        Assert.True(item.GetProperty("isActive").GetBoolean());
        Assert.False(item.GetProperty("isConnected").GetBoolean());
        Assert.Equal(lastSeenAtUtc, item.GetProperty("lastSeenAtUtc").GetDateTimeOffset());
        Assert.DoesNotContain("certificateThumbprint", await administrator.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, auditor.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
        Assert.Equal("no-store", operatorResponse.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("no-store", anonymous.Headers.CacheControl?.ToString());
        using var connectedBody = JsonDocument.Parse(await connectedResponse.Content.ReadAsStringAsync());
        JsonElement connectedItem = Assert.Single(connectedBody.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(connectedItem.GetProperty("isConnected").GetBoolean());
    }

    private async Task<HttpResponseMessage> SendGetAsync(Guid userId, string role, string query = "")
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Path + query);
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", role);
        return await _fixture.Client.SendAsync(request);
    }

    private static X509Certificate2 CreateDeviceCertificate()
    {
        using var key = RSA.Create(2048);
        CertificateRequest request = new("CN=device-session-lifecycle-test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static string GetFingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

}

public sealed class DeviceStatusPaginationTests
{
    private const string Path = "/api/v1/admin/devices";

    [Fact]
    public async Task Authorization_failures_are_rejected_without_caching()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true).WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services => services.AddSingleton<DeviceCommandSessionRegistry>());
        });
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpResponseMessage anonymous = await client.GetAsync(Path);
        using HttpRequestMessage operatorRequest = new(HttpMethod.Get, Path);
        operatorRequest.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        operatorRequest.Headers.Add("X-Test-Role", "notificator");
        using HttpResponseMessage operatorResponse = await client.SendAsync(operatorRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("no-store", anonymous.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
        Assert.Equal("no-store", operatorResponse.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=not-a-number")]
    [InlineData("?pageSize=not-a-number")]
    public async Task Invalid_pagination_is_rejected_without_caching(string query)
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true).WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services => services.AddSingleton<DeviceCommandSessionRegistry>());
        });
        var administratorId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            db.Users.Add(new UserAccount
            {
                Id = administratorId,
                ExternalSubject = "pagination-admin",
                Email = "pagination-admin@example.test"
            });
            Role administrator = new()
            {
                Id = Guid.NewGuid(),
                Code = "admin",
                Name = "Адміністратор"
            };
            db.Roles.Add(administrator);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = administratorId,
                RoleId = administrator.Id
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = new(HttpMethod.Get, Path + query);
        request.Headers.Add("X-Test-User-Id", administratorId.ToString());
        request.Headers.Add("X-Test-Role", "admin");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }
}
