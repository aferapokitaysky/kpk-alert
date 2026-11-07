using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Api.Contracts;
using Watcher.Api.Delivery;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceStatusEndpointTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("notificator")]
    public async Task Local_operator_sees_only_authorized_active_devices_and_live_transport_state(string roleCode)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        var authorizedLocationId = Guid.NewGuid();
        var foreignLocationId = Guid.NewGuid();
        var inactiveLocationId = Guid.NewGuid();
        var connectedDeviceId = Guid.NewGuid();
        var disconnectedDeviceId = Guid.NewGuid();
        var foreignDeviceId = Guid.NewGuid();
        var revokedDeviceId = Guid.NewGuid();
        var inactiveLocationDeviceId = Guid.NewGuid();

        await SeedAsync(factory, userId, roleCode, authorizedLocationId,
            new LocationSeed(authorizedLocationId, "Головний корпус", true,
            [
                NewDevice(connectedDeviceId, authorizedLocationId),
                NewDevice(disconnectedDeviceId, authorizedLocationId),
                NewDevice(revokedDeviceId, authorizedLocationId, revoked: true)
            ]),
            new LocationSeed(foreignLocationId, "Спортивний корпус", true,
                [NewDevice(foreignDeviceId, foreignLocationId)]),
            new LocationSeed(inactiveLocationId, "Неактивний корпус", false,
                [NewDevice(inactiveLocationDeviceId, inactiveLocationId)]));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        await SeedSessionsAsync(factory,
        [
            (connectedDeviceId, now, null),
            (foreignDeviceId, now, null),
            (revokedDeviceId, now, null),
            (inactiveLocationDeviceId, now, null),
            (disconnectedDeviceId, now.AddMinutes(-1), null)
        ]);

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = CreateRequest(userId, roleCode);
        using HttpResponseMessage response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement[] items = body.RootElement.EnumerateArray().ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(2, items.Length);
        Assert.Contains(items, item =>
            item.GetProperty("deviceId").GetGuid() == connectedDeviceId &&
            item.GetProperty("connectionState").GetString() == "connected" &&
            item.GetProperty("locationId").GetGuid() == authorizedLocationId);
        Assert.Contains(items, item =>
            item.GetProperty("deviceId").GetGuid() == disconnectedDeviceId &&
            item.GetProperty("connectionState").GetString() == "offline");
        Assert.DoesNotContain(items, item => item.GetProperty("deviceId").GetGuid() == foreignDeviceId);
        Assert.DoesNotContain(items, item => item.GetProperty("deviceId").GetGuid() == revokedDeviceId);
        Assert.DoesNotContain(items, item => item.GetProperty("deviceId").GetGuid() == inactiveLocationDeviceId);

        await SetDisconnectedAsync(factory, connectedDeviceId, now.AddSeconds(1));
        using HttpRequestMessage disconnectedRequest = CreateRequest(userId);
        using HttpResponseMessage disconnectedResponse = await client.SendAsync(disconnectedRequest);
        using var disconnectedBody = JsonDocument.Parse(
            await disconnectedResponse.Content.ReadAsStringAsync());
        JsonElement disconnectedItem = Assert.Single(disconnectedBody.RootElement.EnumerateArray(), item =>
            item.GetProperty("deviceId").GetGuid() == connectedDeviceId);
        Assert.Equal("offline", disconnectedItem.GetProperty("connectionState").GetString());

    }

    [Fact]
    public async Task User_without_location_roles_gets_an_empty_list()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        await SeedAsync(factory, userId, roleCode: null, roleLocationId: null,
            new LocationSeed(locationId, "Корпус", true, [NewDevice(deviceId, locationId)]));

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = CreateRequest(userId);

        using HttpResponseMessage response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(body.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task Global_administrator_sees_devices_in_all_active_locations()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        var firstLocationId = Guid.NewGuid();
        var secondLocationId = Guid.NewGuid();
        var firstDeviceId = Guid.NewGuid();
        var secondDeviceId = Guid.NewGuid();
        await SeedAsync(factory, userId, roleCode: "admin", roleLocationId: null,
            new LocationSeed(firstLocationId, "Перший корпус", true, [NewDevice(firstDeviceId, firstLocationId)]),
            new LocationSeed(secondLocationId, "Другий корпус", true, [NewDevice(secondDeviceId, secondLocationId)]));

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = CreateRequest(userId, "admin");

        using HttpResponseMessage response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Guid[] returnedDeviceIds = body.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("deviceId").GetGuid())
            .ToArray();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(firstDeviceId, returnedDeviceIds);
        Assert.Contains(secondDeviceId, returnedDeviceIds);
    }

    [Fact]
    public async Task Stale_global_admin_claim_without_database_grant_is_forbidden()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await SeedAsync(factory, userId, roleCode: null, roleLocationId: null,
            new LocationSeed(locationId, "Корпус", true, [NewDevice(Guid.NewGuid(), locationId)]));

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = CreateRequest(userId, "admin");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Inactive_global_administrator_is_forbidden()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await SeedAsync(factory, userId, roleCode: "admin", roleLocationId: null,
            new LocationSeed(locationId, "Корпус", true, [NewDevice(Guid.NewGuid(), locationId)]));
        await DeactivateUserAsync(factory, userId);

        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = CreateRequest(userId, "admin");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Anonymous_request_is_not_cacheable()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage response = await client.GetAsync("/api/v1/device-status");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new AuthTestWebApplicationFactory(googleEnabled: false, useInMemoryDatabase: true)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<DeviceCommandSessionRegistry>()));

    private static HttpRequestMessage CreateRequest(Guid userId, params string[] roles)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/device-status");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (roles.Length > 0)
        {
            request.Headers.Add("X-Test-Role", roles);
        }

        return request;
    }

    private static async Task SeedAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string? roleCode,
        Guid? roleLocationId,
        params LocationSeed[] locationSeeds)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();

        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = $"test-{userId}",
            Email = $"{userId}@example.test"
        });
        db.Locations.AddRange(locationSeeds.Select(location => new Location
        {
            Id = location.Id,
            Code = $"loc-{location.Id:N}",
            Name = location.Name,
            IsActive = location.IsActive
        }));

        if (roleCode is not null)
        {
            Role role = new() { Id = Guid.NewGuid(), Code = roleCode, Name = roleCode };
            db.Roles.Add(role);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = role.Id,
                LocationId = roleLocationId
            });
        }

        db.Devices.AddRange(locationSeeds.SelectMany(location => location.Devices));
        await db.SaveChangesAsync();
    }

    private static async Task DeactivateUserAsync(WebApplicationFactory<Program> factory, Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        UserAccount user = await db.Users.SingleAsync(account => account.Id == userId);
        user.IsActive = false;
        await db.SaveChangesAsync();
    }

    private static async Task SeedSessionsAsync(
        WebApplicationFactory<Program> factory,
        IReadOnlyList<(Guid DeviceId, DateTimeOffset LastSeenAtUtc, DateTimeOffset? DisconnectedAtUtc)> sessions)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.DeviceSessions.AddRange(sessions.Select(session => new DeviceSession
        {
            Id = Guid.NewGuid(),
            DeviceId = session.DeviceId,
            ConnectedAtUtc = session.LastSeenAtUtc,
            LastSeenAtUtc = session.LastSeenAtUtc,
            DisconnectedAtUtc = session.DisconnectedAtUtc
        }));
        await db.SaveChangesAsync();
    }

    private static async Task SetDisconnectedAsync(
        WebApplicationFactory<Program> factory,
        Guid deviceId,
        DateTimeOffset disconnectedAtUtc)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        DeviceSession session = await db.DeviceSessions.SingleAsync(item =>
            item.DeviceId == deviceId && item.DisconnectedAtUtc == null);
        session.DisconnectedAtUtc = disconnectedAtUtc;
        await db.SaveChangesAsync();
    }

    private static Device NewDevice(Guid id, Guid locationId, bool revoked = false) => new()
    {
        Id = id == Guid.Empty ? Guid.NewGuid() : id,
        LocationId = locationId,
        Name = $"Вузол {locationId:N}",
        CertificateThumbprint = Guid.NewGuid().ToString("N"),
        RevokedAtUtc = revoked ? DateTimeOffset.UtcNow : null
    };

    private sealed record LocationSeed(Guid Id, string Name, bool IsActive, IReadOnlyList<Device> Devices);

    private sealed class OpenWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => Task.FromResult(
            new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
