namespace Watcher.Domain.Entities;

public sealed class Device
{
    public Guid Id { get; set; }
    public Guid LocationId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string CertificateThumbprint { get; set; } = string.Empty;
    public DateTimeOffset? RevokedAtUtc { get; set; }
}

public sealed class DeviceSession
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public DateTimeOffset ConnectedAtUtc { get; set; }
    public DateTimeOffset? DisconnectedAtUtc { get; set; }
    public DateTimeOffset LastSeenAtUtc { get; set; }
}
