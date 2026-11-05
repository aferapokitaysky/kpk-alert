namespace Watcher.Domain.Entities;

public sealed class AlertCommand
{
    public const int DefaultValidityMinutes = 5;

    public Guid Id { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string Action { get; set; } = string.Empty;
    public string PresetCode { get; set; } = string.Empty;
    public Guid? InitiatedByUserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public List<CommandTarget> Targets { get; set; } = [];
}

public sealed class CommandTarget
{
    public Guid Id { get; set; }
    public Guid CommandId { get; set; }
    public Guid LocationId { get; set; }
    public string State { get; set; } = "Created";
}
