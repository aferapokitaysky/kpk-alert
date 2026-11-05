namespace Watcher.Domain.Entities;

public sealed class DeviceCommandReceipt
{
    public Guid DeviceId { get; set; }
    public Guid EventId { get; set; }
    public Guid CommandId { get; set; }
    public long Sequence { get; set; }
    public string State { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string? DiagnosticCode { get; set; }
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
