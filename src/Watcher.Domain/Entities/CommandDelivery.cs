namespace Watcher.Domain.Entities;

public enum CommandDeliveryState
{
    Pending,
    InFlight,
    Accepted,
    Playing,
    Completed,
    Stopped,
    Rejected,
    Failed,
    Expired
}

public sealed class CommandDelivery
{
    public Guid Id { get; set; }
    public Guid CommandId { get; set; }
    public Guid DeviceId { get; set; }
    public CommandDeliveryState State { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset? LastAttemptAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public int AttemptCount { get; set; }
    public string? LastWriteOutcome { get; set; }
}
