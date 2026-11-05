namespace Watcher.Domain.Entities;

public sealed class UkraineAlarmWebhookOperation
{
    public const string DefaultIntegrationCode = "ukrainealarm_webhook";

    public Guid Id { get; set; }
    public Guid IdempotencyKey { get; set; }
    public string IntegrationCode { get; set; } = DefaultIntegrationCode;
    public Guid ActorUserId { get; set; }
    public string Action { get; set; } = string.Empty;
    public string State { get; set; } = "Pending";
    public string CallbackUrl { get; set; } = string.Empty;
    public string? OutcomeCode { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class ExternalIntegrationState
{
    public string IntegrationCode { get; set; } = string.Empty;
    public bool? IsRegistered { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
