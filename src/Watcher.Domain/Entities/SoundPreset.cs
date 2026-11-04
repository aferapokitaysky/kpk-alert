namespace Watcher.Domain.Entities;

public sealed class SoundPreset
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Priority { get; set; }
    public string ManifestHash { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
}
