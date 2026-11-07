using System.Security.Cryptography;
using System.Text.Json;
using Watcher.Domain.Entities;

namespace Watcher.Api.AudioAssets;

public sealed record DeviceAudioManifest(
    int SchemaVersion,
    string LocationId,
    IReadOnlyList<DeviceAudioManifestPreset> Presets);

public sealed record DeviceAudioManifestPreset(
    string PresetCode,
    string FileName,
    string Sha256,
    Guid AssetId,
    long SizeBytes,
    int DurationSeconds);

public sealed record SerializedDeviceAudioManifest(byte[] Content, string Sha256);

public static class DeviceAudioManifestFactory
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static SerializedDeviceAudioManifest Create(Guid locationId, LocationAudioAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (locationId == Guid.Empty || asset.LocationId != locationId)
        {
            throw new ArgumentException("Аудіопакет має належати запитаній локації.", nameof(asset));
        }

        DeviceAudioManifest manifest = new(
            1,
            locationId.ToString("D"),
            [new DeviceAudioManifestPreset(
                asset.PresetCode,
                $"{asset.PresetCode}.wav",
                asset.Sha256,
                asset.Id,
                asset.SizeBytes,
                asset.DurationSeconds)]);
        var content = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        return new SerializedDeviceAudioManifest(content, Convert.ToHexString(SHA256.HashData(content)));
    }
}
