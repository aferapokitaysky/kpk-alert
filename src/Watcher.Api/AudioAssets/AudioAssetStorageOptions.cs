namespace Watcher.Api.AudioAssets;

public sealed class AudioAssetStorageOptions
{
    public string StoragePath { get; set; } = Path.Combine(AppContext.BaseDirectory, "audio-assets");
}
