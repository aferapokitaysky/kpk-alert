using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class DeviceSessionPresenceServiceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Open_session_rejects_state_changed_after_certificate_authentication(
        bool revokeDevice,
        bool deactivateLocation)
    {
        await using ServiceProvider provider = CreateProvider();
        DeviceSessionPresenceService presence = CreatePresenceService(provider);
        var deviceId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        using X509Certificate2 certificate = CreateCertificate();

        await SeedDeviceAsync(provider, deviceId, locationId, GetFingerprint(certificate));
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            DeviceCertificateAuthenticator authenticator = new(db, TimeProvider.System);
            DeviceIdentity? identity = await authenticator.AuthenticateAsync(
                certificate, CancellationToken.None);

            Assert.Equal(new DeviceIdentity(deviceId, locationId), identity);

            await ChangeDeviceAvailabilityAsync(db, deviceId, locationId, revokeDevice, deactivateLocation);
        }

        await Assert.ThrowsAnyAsync<DeviceSessionUnavailableException>(() =>
            presence.OpenSessionAsync(deviceId, CancellationToken.None));

        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            Assert.Empty(await db.DeviceSessions.ToArrayAsync());
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Registration_rejects_session_when_device_becomes_unavailable(
        bool revokeDevice,
        bool deactivateLocation)
    {
        await using ServiceProvider provider = CreateProvider();
        DeviceSessionPresenceService presence = CreatePresenceService(provider);
        var deviceId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await SeedDeviceAsync(provider, deviceId, locationId, "test-fingerprint");

        Guid sessionId = await presence.OpenSessionAsync(deviceId, CancellationToken.None);
        await using (AsyncServiceScope scope = provider.CreateAsyncScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            await ChangeDeviceAvailabilityAsync(db, deviceId, locationId, revokeDevice, deactivateLocation);
        }

        Assert.False(await presence.IsSessionActiveAsync(sessionId, CancellationToken.None));

        DeviceCommandSessionRegistry sessions = new();
        DeviceCommandWebSocketSession? session = await sessions.TryRegisterIfCurrentAsync(
            deviceId,
            sessionId,
            new TestWebSocket(),
            token => presence.IsSessionActiveAsync(sessionId, token),
            CancellationToken.None);

        Assert.Null(session);
        Assert.Empty(sessions.ConnectedDeviceIds);
    }

    private static ServiceProvider CreateProvider()
    {
        ServiceCollection services = new();
        var databaseName = Guid.NewGuid().ToString();
        services.AddLogging();
        services.AddDbContext<WatcherDbContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private static DeviceSessionPresenceService CreatePresenceService(IServiceProvider provider) =>
        new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<DeviceSessionPresenceService>.Instance);

    private static async Task SeedDeviceAsync(
        IServiceProvider provider,
        Guid deviceId,
        Guid locationId,
        string fingerprint)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.Locations.Add(new Location
        {
            Id = locationId,
            Code = $"session-test-{locationId:N}",
            Name = "Тестовий корпус",
            IsActive = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.Devices.Add(new Device
        {
            Id = deviceId,
            LocationId = locationId,
            Name = "Тестовий вузол",
            CertificateThumbprint = fingerprint
        });
        await db.SaveChangesAsync();
    }

    private static async Task ChangeDeviceAvailabilityAsync(
        WatcherDbContext db,
        Guid deviceId,
        Guid locationId,
        bool revokeDevice,
        bool deactivateLocation)
    {
        if (revokeDevice)
        {
            Device device = await db.Devices.SingleAsync(item => item.Id == deviceId);
            device.RevokedAtUtc = DateTimeOffset.UtcNow;
        }

        if (deactivateLocation)
        {
            Location location = await db.Locations.SingleAsync(item => item.Id == locationId);
            location.IsActive = false;
        }

        await db.SaveChangesAsync();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        CertificateRequest request = new("CN=session-lifecycle-test", key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static string GetFingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private sealed class TestWebSocket : WebSocket
    {
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => WebSocketState.Open;
        public override string? SubProtocol => null;
        public override void Abort() { }
        public override void Dispose() { }
        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
