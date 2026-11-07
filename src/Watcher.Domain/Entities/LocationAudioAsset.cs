namespace Watcher.Domain.Entities;

public sealed class LocationAudioAsset
{
    public Guid Id { get; set; }
    public Guid LocationId { get; set; }
    public string PresetCode { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int DurationSeconds { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
}
