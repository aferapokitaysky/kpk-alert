namespace Watcher.Domain.Entities;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public Guid CommandId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = "{}";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? ProcessedAtUtc { get; set; }
    public int Attempts { get; set; }
}
