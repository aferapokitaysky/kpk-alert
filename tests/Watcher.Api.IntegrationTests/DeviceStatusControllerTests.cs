using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Api.Delivery;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceStatusControllerTests : IClassFixture<PostgresApiFixture>
{
    private const string Path = "/api/v1/admin/devices";
    private readonly PostgresApiFixture _fixture;

    public DeviceStatusControllerTests(PostgresApiFixture fixture) => _fixture = fixture;

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

        OpenWebSocket socket = new();
        DeviceCommandWebSocketSession session = await _fixture.DeviceSessions.RegisterAsync(
            deviceId, socket, CancellationToken.None);
        using HttpResponseMessage connectedResponse = await SendGetAsync(seed.AdministratorId, "admin");
        _fixture.DeviceSessions.Unregister(session);

        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
        Assert.Equal("no-store", administrator.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await administrator.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());
        Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(1, root.GetProperty("totalCount").GetInt32());
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
