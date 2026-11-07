using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = DeviceCertificateAuthentication.PendingScheme)]
[NoStoreResponse]
[Route("api/v1/device/certificate-rotation")]
public sealed class DeviceCertificateRotationController(
    WatcherDbContext db,
    DeviceCommandSessionRegistry sessions,
    TimeProvider timeProvider) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpPost("confirm")]
    public async Task<IActionResult> ConfirmAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirst(DeviceCertificateAuthentication.DeviceIdClaim)?.Value,
                out Guid deviceId))
        {
            return Unauthorized();
        }

        using X509Certificate2? certificate = await HttpContext.Connection.GetClientCertificateAsync(cancellationToken);
        if (certificate is null)
        {
            return Unauthorized();
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(certificate.RawData));
        DateTimeOffset rotatedAtUtc = timeProvider.GetUtcNow();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(721934106)", cancellationToken);

        Device? device = await db.Devices
            .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null || device.RevokedAtUtc is not null ||
            device.PendingCertificateThumbprint != fingerprint ||
            !await db.Locations.AnyAsync(location => location.Id == device.LocationId && location.IsActive,
                cancellationToken))
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Очікувана ротація відсутня або більше не може бути підтверджена."
            });
        }

        var oldFingerprint = device.CertificateThumbprint;
        device.CertificateThumbprint = fingerprint;
        device.PendingCertificateThumbprint = null;
        device.PendingCertificateCreatedAtUtc = null;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "device.certificate_rotated",
            ActorType = "device",
            ActorId = device.Id,
            Payload = JsonSerializer.Serialize(new
            {
                deviceId = device.Id,
                device.Name,
                device.LocationId,
                rotatedAtUtc
            }, JsonOptions),
            OccurredAtUtc = rotatedAtUtc
        });

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await sessions.DisconnectAsync(deviceId, CancellationToken.None);

        return Ok(new DeviceCertificateRotationConfirmedResponse(deviceId, rotatedAtUtc));
    }
}

public sealed record DeviceCertificateRotationConfirmedResponse(Guid DeviceId, DateTimeOffset RotatedAtUtc);
