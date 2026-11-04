namespace Watcher.Domain.Entities;

public sealed class AuditEvent
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string ActorType { get; set; } = string.Empty;
    public Guid? ActorId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTimeOffset OccurredAtUtc { get; set; }
}
