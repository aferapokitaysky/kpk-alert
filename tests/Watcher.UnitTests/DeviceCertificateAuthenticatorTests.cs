using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class DeviceCertificateAuthenticatorTests
{
    [Fact]
    public async Task Authenticate_returns_identity_for_registered_certificate_and_active_location()
    {
        DateTimeOffset now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = CreateCertificate(now.AddMinutes(-1), now.AddMinutes(5));
        await using WatcherDbContext db = CreateDbContext();
        var deviceId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        await AddDeviceAsync(db, deviceId, locationId, GetFingerprint(certificate));

        var authenticator = new DeviceCertificateAuthenticator(db, new FixedTimeProvider(now));
        DeviceIdentity? identity = await authenticator.AuthenticateAsync(certificate, CancellationToken.None);

        Assert.Equal(new DeviceIdentity(deviceId, locationId), identity);
    }

    [Fact]
    public async Task Authenticate_rejects_a_revoked_device()
    {
        DateTimeOffset now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = CreateCertificate(now.AddMinutes(-1), now.AddMinutes(5));
        await using WatcherDbContext db = CreateDbContext();
        await AddDeviceAsync(db, Guid.NewGuid(), Guid.NewGuid(), GetFingerprint(certificate), now);

        var authenticator = new DeviceCertificateAuthenticator(db, new FixedTimeProvider(now));

        Assert.Null(await authenticator.AuthenticateAsync(certificate, CancellationToken.None));
    }

    [Fact]
    public async Task Authenticate_rejects_a_device_in_an_inactive_location()
    {
        DateTimeOffset now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = CreateCertificate(now.AddMinutes(-1), now.AddMinutes(5));
        await using WatcherDbContext db = CreateDbContext();
        await AddDeviceAsync(db, Guid.NewGuid(), Guid.NewGuid(), GetFingerprint(certificate), locationIsActive: false);

        var authenticator = new DeviceCertificateAuthenticator(db, new FixedTimeProvider(now));

        Assert.Null(await authenticator.AuthenticateAsync(certificate, CancellationToken.None));
    }

    [Theory]
    [InlineData(-10, -1)]
    [InlineData(1, 10)]
    public async Task Authenticate_rejects_a_certificate_outside_its_validity_window(int notBeforeOffset, int notAfterOffset)
    {
        DateTimeOffset now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
        using X509Certificate2 certificate = CreateCertificate(
            now.AddMinutes(notBeforeOffset), now.AddMinutes(notAfterOffset));
        await using WatcherDbContext db = CreateDbContext();
        await AddDeviceAsync(db, Guid.NewGuid(), Guid.NewGuid(), GetFingerprint(certificate));

        var authenticator = new DeviceCertificateAuthenticator(db, new FixedTimeProvider(now));

        Assert.Null(await authenticator.AuthenticateAsync(certificate, CancellationToken.None));
    }

    private static async Task AddDeviceAsync(
        WatcherDbContext db,
        Guid deviceId,
        Guid locationId,
        string thumbprint,
        DateTimeOffset? revokedAtUtc = null,
        bool locationIsActive = true)
    {
        db.Locations.Add(new Location
        {
            Id = locationId,
            Code = $"location-{locationId:N}",
            Name = "Тестовий корпус",
            IsActive = locationIsActive,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        db.Devices.Add(new Device
        {
            Id = deviceId,
            LocationId = locationId,
            Name = "Тестовий вузол",
            CertificateThumbprint = thumbprint,
            RevokedAtUtc = revokedAtUtc
        });
        await db.SaveChangesAsync();
    }

    private static WatcherDbContext CreateDbContext()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WatcherDbContext(options);
    }

    private static X509Certificate2 CreateCertificate(DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=kgta-device-test", key, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    private static string GetFingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
