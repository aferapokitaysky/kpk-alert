using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/admin/devices")]
public sealed class DeviceAdministrationController(
    WatcherDbContext db,
    DeviceCommandSessionRegistry sessions,
    TimeProvider timeProvider) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex CertificateFingerprintPattern = new(
        "^[0-9a-fA-F]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterAsync(
        [FromBody] RegisterDeviceRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        var name = request.Name?.Trim() ?? string.Empty;
        var fingerprint = request.CertificateSha256?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 120 || request.LocationId == Guid.Empty ||
            !CertificateFingerprintPattern.IsMatch(fingerprint))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте назву вузла, корпус і SHA-256 відбиток сертифіката.");
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAccessMutationsAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        var locationExists = await db.Locations.AnyAsync(
            location => location.Id == request.LocationId && location.IsActive,
            cancellationToken);
        if (!locationExists)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Оберіть наявний активний корпус.");
        }

        var normalizedFingerprint = fingerprint.ToUpperInvariant();
        if (await db.Devices.AnyAsync(device =>
                device.CertificateThumbprint.ToUpper() == normalizedFingerprint ||
                (device.PendingCertificateThumbprint != null &&
                 device.PendingCertificateThumbprint.ToUpper() == normalizedFingerprint), cancellationToken))
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Цей сертифікат уже призначено вузлу або очікується для ротації."
            });
        }

        Device device = new()
        {
            Id = Guid.NewGuid(),
            LocationId = request.LocationId,
            Name = name,
            CertificateThumbprint = normalizedFingerprint
        };
        db.Devices.Add(device);
        AddAuditEvent("device.registered", new
        {
            deviceId = device.Id,
            device.Name,
            device.LocationId
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Пристрій із таким сертифікатом уже зареєстрований."
            });
        }

        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, new DeviceRegistrationResponse(
            device.Id, device.Name, device.LocationId, IsActive: true));
    }

    [HttpPost("{deviceId:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        DateTimeOffset revokedAtUtc;
        await using (IDbContextTransaction transaction =
                     await db.Database.BeginTransactionAsync(cancellationToken))
        {
            await LockUserAccessMutationsAsync(cancellationToken);
            if (!await IsGlobalAdministratorAsync(cancellationToken))
            {
                return Forbid();
            }

            Device? device = await db.Devices
                .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
            if (device is null)
            {
                return NotFound();
            }

            if (device.RevokedAtUtc is null)
            {
                revokedAtUtc = timeProvider.GetUtcNow();
                device.RevokedAtUtc = revokedAtUtc;
                await db.SaveChangesAsync(cancellationToken);
                await db.Entry(device).ReloadAsync(cancellationToken);
                revokedAtUtc = device.RevokedAtUtc!.Value;
                AddAuditEvent("device.revoked", new
                {
                    deviceId = device.Id,
                    device.Name,
                    device.LocationId,
                    revokedAtUtc
                });
                await db.SaveChangesAsync(cancellationToken);
            }
            else
            {
                revokedAtUtc = device.RevokedAtUtc.Value;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        await sessions.DisconnectAsync(deviceId, CancellationToken.None);
        return Ok(new DeviceRevocationResponse(deviceId, IsActive: false, revokedAtUtc));
    }

    [HttpPost("{deviceId:guid}/certificate-rotations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StageCertificateRotationAsync(
        Guid deviceId,
        [FromBody] StageDeviceCertificateRotationRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        var fingerprint = request.CertificateSha256?.Trim() ?? string.Empty;
        if (!CertificateFingerprintPattern.IsMatch(fingerprint))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Вкажіть SHA-256 відбиток сертифіката у форматі з 64 шістнадцяткових символів.");
        }

        DateTimeOffset createdAtUtc = timeProvider.GetUtcNow();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAccessMutationsAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        Device? device = await db.Devices
            .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        if (device.RevokedAtUtc is not null ||
            !await db.Locations.AnyAsync(location => location.Id == device.LocationId && location.IsActive,
                cancellationToken))
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Для відкликаного вузла або неактивного корпусу ротація недоступна."
            });
        }

        var normalizedFingerprint = fingerprint.ToUpperInvariant();
        if (device.PendingCertificateThumbprint is not null)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Для вузла вже очікується підтвердження ротації. Спочатку скасуйте його."
            });
        }

        if (device.CertificateThumbprint.ToUpper() == normalizedFingerprint ||
            await db.Devices.AnyAsync(existing => existing.Id != deviceId &&
                (existing.CertificateThumbprint.ToUpper() == normalizedFingerprint ||
                 (existing.PendingCertificateThumbprint != null &&
                  existing.PendingCertificateThumbprint.ToUpper() == normalizedFingerprint)), cancellationToken))
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Цей сертифікат уже призначено іншому вузлу або він є активним."
            });
        }

        device.PendingCertificateThumbprint = normalizedFingerprint;
        device.PendingCertificateCreatedAtUtc = createdAtUtc;
        AddAuditEvent("device.certificate_rotation_staged", new
        {
            deviceId = device.Id,
            device.Name,
            device.LocationId,
            createdAtUtc
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Accepted(new DeviceCertificateRotationResponse(device.Id, createdAtUtc));
    }

    [HttpDelete("{deviceId:guid}/certificate-rotations")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CancelCertificateRotationAsync(
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAccessMutationsAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        Device? device = await db.Devices
            .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (device is null)
        {
            return NotFound();
        }

        if (device.PendingCertificateThumbprint is not null)
        {
            device.PendingCertificateThumbprint = null;
            device.PendingCertificateCreatedAtUtc = null;
            AddAuditEvent("device.certificate_rotation_cancelled", new
            {
                deviceId = device.Id,
                device.Name,
                device.LocationId
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> IsGlobalAdministratorAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return false;
        }

        return await (from assignment in db.UserRoles.AsNoTracking()
                      join account in db.Users.AsNoTracking() on assignment.UserId equals account.Id
                      join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                      where assignment.UserId == userId && assignment.LocationId == null &&
                            account.IsActive && role.Code == "admin"
                      select assignment.Id).AnyAsync(cancellationToken);
    }

    private async Task LockUserAccessMutationsAsync(CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(721934106)", cancellationToken);

    private void AddAuditEvent(string eventType, object payload)
    {
        var actorId = Guid.Parse(User.FindFirstValue("kgta_user_id")!);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorType = "user",
            ActorId = actorId,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_devices_CertificateThumbprint"
        };
}

public sealed record RegisterDeviceRequest(string Name, Guid LocationId, string CertificateSha256);

public sealed record DeviceRegistrationResponse(Guid DeviceId, string Name, Guid LocationId, bool IsActive);

public sealed record DeviceRevocationResponse(Guid DeviceId, bool IsActive, DateTimeOffset RevokedAtUtc);

public sealed record StageDeviceCertificateRotationRequest(string CertificateSha256);

public sealed record DeviceCertificateRotationResponse(Guid DeviceId, DateTimeOffset PendingAtUtc);
