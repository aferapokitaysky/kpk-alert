using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceAdministrationEndpointTests(PostgresApiFixture fixture)
    : IClassFixture<PostgresApiFixture>
{
    private const string Endpoint = "/api/v1/admin/devices";

    [PostgresFact]
    public async Task Administrator_registers_a_device_and_revokes_its_certificate_once()
    {
        SeedData seed = await fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        HttpClient client = fixture.Client;
        string fingerprint = new('a', 64);

        using HttpResponseMessage registered = await SendAsync(
            client,
            seed.AdministratorId,
            HttpMethod.Post,
            Endpoint,
            new RegisterDeviceRequest("  Пульт першого корпусу  ", seed.FirstLocationId, fingerprint));

        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        Assert.Null(registered.Headers.Location);
        Assert.Equal("no-store", registered.Headers.CacheControl?.ToString());
        using var registrationBody = JsonDocument.Parse(await registered.Content.ReadAsStringAsync());
        Guid deviceId = registrationBody.RootElement.GetProperty("deviceId").GetGuid();
        Assert.Equal("Пульт першого корпусу", registrationBody.RootElement.GetProperty("name").GetString());
        Assert.DoesNotContain(fingerprint, registrationBody.RootElement.GetRawText());
        Assert.DoesNotContain("certificateThumbprint", registrationBody.RootElement.GetRawText());

        using WatcherDbContext db = fixture.CreateDbContext();
        Device device = await db.Devices.SingleAsync();
        Assert.Equal(deviceId, device.Id);
        Assert.Equal(fingerprint.ToUpperInvariant(), device.CertificateThumbprint);
        Assert.Null(device.RevokedAtUtc);
        AuditEvent registrationAudit = await db.AuditEvents.SingleAsync();
        Assert.Equal("device.registered", registrationAudit.EventType);
        Assert.Equal(seed.AdministratorId, registrationAudit.ActorId);
        Assert.DoesNotContain(fingerprint, registrationAudit.Payload);

        OpenWebSocket socket = new();
        _ = await fixture.DeviceSessions.RegisterAsync(deviceId, Guid.NewGuid(), socket, CancellationToken.None);
        using HttpResponseMessage revoked = await SendAsync(
            client, seed.AdministratorId, HttpMethod.Post, $"{Endpoint}/{deviceId}/revoke");
        using var revocationBody = JsonDocument.Parse(await revoked.Content.ReadAsStringAsync());
        DateTimeOffset revokedAtUtc = revocationBody.RootElement.GetProperty("revokedAtUtc").GetDateTimeOffset();

        Assert.Equal(HttpStatusCode.OK, revoked.StatusCode);
        Assert.Equal("no-store", revoked.Headers.CacheControl?.ToString());
        Assert.Equal(deviceId, revocationBody.RootElement.GetProperty("deviceId").GetGuid());
        Assert.False(revocationBody.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, socket.CloseStatus);
        Assert.DoesNotContain(deviceId, fixture.DeviceSessions.ConnectedDeviceIds);

        using HttpResponseMessage repeatedRevocation = await SendAsync(
            client, seed.AdministratorId, HttpMethod.Post, $"{Endpoint}/{deviceId}/revoke");
        using var repeatedBody = JsonDocument.Parse(await repeatedRevocation.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, repeatedRevocation.StatusCode);
        Assert.Equal(revokedAtUtc,
            repeatedBody.RootElement.GetProperty("revokedAtUtc").GetDateTimeOffset());

        await db.Entry(device).ReloadAsync();
        Assert.Equal(revokedAtUtc, device.RevokedAtUtc);
        AuditEvent[] auditEvents = await db.AuditEvents.OrderBy(item => item.EventType).ToArrayAsync();
        Assert.Equal(2, auditEvents.Length);
        Assert.Contains(auditEvents, item => item.EventType == "device.revoked");
    }

    [PostgresFact]
    public async Task Registration_rejects_invalid_certificate_and_duplicate_fingerprint()
    {
        SeedData seed = await fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        HttpClient client = fixture.Client;

        using HttpResponseMessage invalid = await SendAsync(
            client,
            seed.AdministratorId,
            HttpMethod.Post,
            Endpoint,
            new RegisterDeviceRequest("Вузол", seed.FirstLocationId, "not-a-sha256-fingerprint"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

        string fingerprint = new('b', 64);
        using HttpResponseMessage first = await SendAsync(
            client,
            seed.AdministratorId,
            HttpMethod.Post,
            Endpoint,
            new RegisterDeviceRequest("Вузол 01", seed.FirstLocationId, fingerprint));
        using HttpResponseMessage duplicate = await SendAsync(
            client,
            seed.AdministratorId,
            HttpMethod.Post,
            Endpoint,
            new RegisterDeviceRequest("Вузол 02", seed.SecondLocationId, fingerprint));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        using WatcherDbContext db = fixture.CreateDbContext();
        Assert.Single(await db.Devices.ToArrayAsync());
        Assert.Single(await db.AuditEvents.ToArrayAsync());
    }

    [PostgresFact]
    public async Task Device_mutations_require_a_current_global_administrator_and_csrf()
    {
        SeedData seed = await fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        using WatcherDbContext db = fixture.CreateDbContext();
        Device device = new()
        {
            Id = Guid.NewGuid(),
            LocationId = seed.FirstLocationId,
            Name = "Вузол 01",
            CertificateThumbprint = new('c', 64)
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync();

        HttpClient client = fixture.Client;
        using HttpResponseMessage forbidden = await SendAsync(
            client,
            seed.OperatorId,
            HttpMethod.Post,
            $"{Endpoint}/{device.Id}/revoke",
            role: "admin");
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using HttpRequestMessage withoutCsrf = new(HttpMethod.Post, $"{Endpoint}/{device.Id}/revoke");
        withoutCsrf.Headers.Add("X-Test-User-Id", seed.AdministratorId.ToString());
        withoutCsrf.Headers.Add("X-Test-Role", "admin");
        using HttpResponseMessage csrfRejected = await client.SendAsync(withoutCsrf);
        Assert.Equal(HttpStatusCode.BadRequest, csrfRejected.StatusCode);

        await db.Entry(device).ReloadAsync();
        Assert.Null(device.RevokedAtUtc);
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Guid userId,
        HttpMethod method,
        string path,
        object? body = null,
        string role = "admin")
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", role);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, userId, role));
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client, Guid userId, string role)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", role);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("CSRF token was missing from the response.");
    }

    private sealed class OpenWebSocket : WebSocket
    {
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;
        private WebSocketState _state = WebSocketState.Open;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription,
            CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);
        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => Task.FromResult(
            new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType,
            bool endOfMessage, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
