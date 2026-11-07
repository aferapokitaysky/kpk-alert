using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Watcher.Api.AudioAssets;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/admin/locations/{locationId:guid}/sound-presets/{presetCode}/asset")]
public sealed class LocationAudioAssetAdministrationController(
    WatcherDbContext db,
    PhysicalAudioAssetStore assets,
    TimeProvider timeProvider,
    ILogger<LocationAudioAssetAdministrationController> logger) : ControllerBase
{
    private static readonly Regex PresetCodePattern = new("^[a-z0-9_]{1,80}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet]
    public async Task<IActionResult> GetAsync(
        Guid locationId,
        string presetCode,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!PresetCodePattern.IsMatch(presetCode) || locationId == Guid.Empty)
        {
            return ValidationProblem("Перевірте код сценарію та ідентифікатор локації.");
        }

        LocationAudioAsset? asset = await db.LocationAudioAssets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.LocationId == locationId && item.PresetCode == presetCode,
                cancellationToken);
        return asset is null
            ? NotFound()
            : Ok(new LocationAudioAssetResponse(
                asset.Id, asset.LocationId, asset.PresetCode, asset.Sha256, asset.SizeBytes,
                asset.DurationSeconds));
    }

    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(PhysicalAudioAssetStore.MaximumFileBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = PhysicalAudioAssetStore.MaximumFileBytes + 1024 * 1024)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UploadAsync(
        Guid locationId,
        string presetCode,
        [FromForm] UploadLocationAudioAssetRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!PresetCodePattern.IsMatch(presetCode) || locationId == Guid.Empty ||
            request.File is null || request.File.Length is <= 0 or > PhysicalAudioAssetStore.MaximumFileBytes)
        {
            return ValidationProblem("Оберіть сценарій, активну локацію та WAV-файл розміром до 100 MiB.");
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        Location? location = await db.Locations
            .FromSqlInterpolated($"SELECT * FROM locations WHERE \"Id\" = {locationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (location is null || !location.IsActive)
        {
            return NotFound();
        }

        SoundPreset? preset = await db.SoundPresets
            .FromSqlInterpolated($"SELECT * FROM sound_presets WHERE \"Code\" = {presetCode} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (preset is null)
        {
            return NotFound();
        }

        if (preset.IsPublished)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Зніміть публікацію сценарію перед заміною його аудіофайла."
            });
        }

        LocationAudioAsset? existingAsset = await db.LocationAudioAssets
            .SingleOrDefaultAsync(asset => asset.LocationId == locationId && asset.PresetCode == presetCode,
                cancellationToken);
        var assetId = Guid.NewGuid();
        StoredAudioAsset storedAsset;
        await using (Stream fileStream = request.File.OpenReadStream())
        {
            try
            {
                storedAsset = await assets.SaveAsync(locationId, assetId, fileStream, cancellationToken);
            }
            catch (InvalidAudioAssetException exception)
            {
                return ValidationProblem(exception.Message);
            }
        }

        try
        {
            if (existingAsset is not null)
            {
                db.LocationAudioAssets.Remove(existingAsset);
                await db.SaveChangesAsync(cancellationToken);
            }

            LocationAudioAsset asset = new()
            {
                Id = assetId,
                LocationId = locationId,
                PresetCode = presetCode,
                Sha256 = storedAsset.Sha256,
                SizeBytes = storedAsset.SizeBytes,
                DurationSeconds = storedAsset.DurationSeconds,
                CreatedAtUtc = timeProvider.GetUtcNow()
            };
            db.LocationAudioAssets.Add(asset);
            AddAuditEvent("audio_asset.uploaded", locationId, presetCode, asset);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsAssetUniqueViolation(exception))
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await DeleteUncommittedAssetAsync(locationId, assetId);
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Аудіофайл сценарію змінився паралельно. Оновіть сторінку та повторіть завантаження."
            });
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await DeleteUncommittedAssetAsync(locationId, assetId);
            logger.LogError(exception, "Не вдалося записати аудіофайл сценарію {PresetCode} для локації {LocationId}.",
                presetCode, locationId);
            throw;
        }

        await transaction.CommitAsync(cancellationToken);
        if (existingAsset is not null)
        {
            await DeleteObsoleteAssetAsync(locationId, existingAsset.Id);
        }

        return Ok(new LocationAudioAssetResponse(
            assetId, locationId, presetCode, storedAsset.Sha256, storedAsset.SizeBytes,
            storedAsset.DurationSeconds));
    }

    [HttpDelete]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAsync(
        Guid locationId,
        string presetCode,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!PresetCodePattern.IsMatch(presetCode) || locationId == Guid.Empty)
        {
            return ValidationProblem("Перевірте код сценарію та ідентифікатор локації.");
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        _ = await db.Locations
            .FromSqlInterpolated($"SELECT * FROM locations WHERE \"Id\" = {locationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        SoundPreset? preset = await db.SoundPresets
            .FromSqlInterpolated($"SELECT * FROM sound_presets WHERE \"Code\" = {presetCode} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (preset is null)
        {
            return NotFound();
        }

        if (preset.IsPublished)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Зніміть публікацію сценарію перед видаленням його аудіофайла."
            });
        }

        LocationAudioAsset? asset = await db.LocationAudioAssets
            .SingleOrDefaultAsync(item => item.LocationId == locationId && item.PresetCode == presetCode,
                cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        db.LocationAudioAssets.Remove(asset);
        AddAuditEvent("audio_asset.deleted", locationId, presetCode, asset);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await DeleteObsoleteAssetAsync(locationId, asset.Id);
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

    private void AddAuditEvent(string eventType, Guid locationId, string presetCode, LocationAudioAsset asset)
    {
        var actorId = Guid.Parse(User.FindFirstValue("kgta_user_id")!);
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorType = "user",
            ActorId = actorId,
            Payload = JsonSerializer.Serialize(new
            {
                locationId,
                presetCode,
                assetId = asset.Id,
                asset.Sha256,
                asset.SizeBytes,
                asset.DurationSeconds
            }, JsonOptions),
            OccurredAtUtc = timeProvider.GetUtcNow()
        });
    }

    private async Task DeleteUncommittedAssetAsync(Guid locationId, Guid assetId)
    {
        try
        {
            await assets.DeleteAsync(locationId, assetId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogError(exception, "Не вдалося видалити незареєстрований аудіофайл {AssetId}.", assetId);
        }
    }

    private async Task DeleteObsoleteAssetAsync(Guid locationId, Guid assetId)
    {
        try
        {
            await assets.DeleteAsync(locationId, assetId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Метадані аудіофайла {AssetId} видалені, але сам файл не вдалося прибрати.",
                assetId);
        }
    }

    private static bool IsAssetUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_location_audio_assets_LocationId_PresetCode"
        };
}

public sealed class UploadLocationAudioAssetRequest
{
    public IFormFile? File { get; init; }
}

public sealed record LocationAudioAssetResponse(
    Guid AssetId,
    Guid LocationId,
    string PresetCode,
    string Sha256,
    long SizeBytes,
    int DurationSeconds);
