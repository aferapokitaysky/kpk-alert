using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.EntityFrameworkCore;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Security;

public sealed record DeviceIdentity(Guid DeviceId, Guid LocationId);

public sealed class DeviceCertificateAuthenticator(
    WatcherDbContext db,
    TimeProvider clock)
{
    public async Task<DeviceIdentity?> AuthenticateAsync(
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        DateTimeOffset now = clock.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            return null;
        }

        var certificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));

        return await (
            from device in db.Devices.AsNoTracking()
            join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
            where device.CertificateThumbprint == certificateFingerprint &&
                  device.RevokedAtUtc == null &&
                  location.IsActive
            select new DeviceIdentity(device.Id, device.LocationId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<DeviceIdentity?> AuthenticatePendingAsync(
        X509Certificate2 certificate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        DateTimeOffset now = clock.GetUtcNow();
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime())
        {
            return null;
        }

        var certificateFingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));

        return await (
            from device in db.Devices.AsNoTracking()
            join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
            where device.PendingCertificateThumbprint == certificateFingerprint &&
                  device.RevokedAtUtc == null &&
                  location.IsActive
            select new DeviceIdentity(device.Id, device.LocationId))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
