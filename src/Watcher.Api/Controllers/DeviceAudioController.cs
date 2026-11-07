using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.AudioAssets;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = DeviceCertificateAuthentication.Scheme)]
[NoStoreResponse]
[Route("api/v1/device/audio")]
public sealed class DeviceAudioController(
    WatcherDbContext db,
    PhysicalAudioAssetStore assets) : ControllerBase
{
    [HttpGet("presets/{presetCode}/manifest")]
    public async Task<IActionResult> GetManifestAsync(string presetCode, CancellationToken cancellationToken)
    {
        if (!TryGetLocationId(out Guid locationId) || !IsValidPresetCode(presetCode))
        {
            return NotFound();
        }

        LocationAudioAsset? asset = await (
            from audioAsset in db.LocationAudioAssets.AsNoTracking()
            join preset in db.SoundPresets.AsNoTracking() on audioAsset.PresetCode equals preset.Code
            join location in db.Locations.AsNoTracking() on audioAsset.LocationId equals location.Id
            where audioAsset.LocationId == locationId && audioAsset.PresetCode == presetCode &&
                  preset.IsPublished && location.IsActive
            select audioAsset)
            .SingleOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        SerializedDeviceAudioManifest manifest = DeviceAudioManifestFactory.Create(locationId, asset);
        Response.Headers.ETag = $"\"{manifest.Sha256}\"";
        Response.Headers.CacheControl = "private, no-store";
        return File(manifest.Content, "application/json; charset=utf-8");
    }

    [HttpGet("assets/{assetId:guid}")]
    public async Task<IActionResult> DownloadAsync(Guid assetId, CancellationToken cancellationToken)
    {
        if (!TryGetLocationId(out Guid locationId))
        {
            return NotFound();
        }

        LocationAudioAsset? asset = await (
            from audioAsset in db.LocationAudioAssets.AsNoTracking()
            join preset in db.SoundPresets.AsNoTracking() on audioAsset.PresetCode equals preset.Code
            join location in db.Locations.AsNoTracking() on audioAsset.LocationId equals location.Id
            where audioAsset.Id == assetId && audioAsset.LocationId == locationId &&
                  preset.IsPublished && location.IsActive
            select audioAsset)
            .SingleOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return NotFound();
        }

        FileStream? stream = assets.OpenRead(locationId, assetId);
        if (stream is null || stream.Length != asset.SizeBytes)
        {
            stream?.Dispose();
            return NotFound();
        }

        Response.Headers.ETag = $"\"{asset.Sha256}\"";
        Response.Headers.CacheControl = "private, no-store";
        return File(stream, "audio/wav", $"{asset.PresetCode}.wav", enableRangeProcessing: true);
    }

    private bool TryGetLocationId(out Guid locationId) =>
        Guid.TryParse(User.FindFirst(DeviceCertificateAuthentication.LocationIdClaim)?.Value, out locationId);

    private static bool IsValidPresetCode(string value) =>
        value.Length is > 0 and <= 80 && value.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
