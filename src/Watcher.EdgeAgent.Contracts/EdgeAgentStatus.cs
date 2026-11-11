namespace Watcher.EdgeAgent.Contracts;

public enum EdgeAgentConnectionState
{
    NotConfigured,
    Starting,
    Connecting,
    Streaming,
    Reconnecting,
    Degraded,
    Stopped
}

public sealed record EdgeAgentStatus(
    int SchemaVersion,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? LastStartedAtUtc,
    EdgeAgentConnectionState ConnectionState,
    Guid? DeviceId,
    Guid? LocationId,
    string AudioState,
    string? AudioEndpointId,
    Guid? LastCommandId,
    long? LastSequence,
    string? LastCommandState,
    int PendingReceiptCount,
    string? DiagnosticCode);

public sealed record EdgeAgentPipeRequest(int SchemaVersion, string Operation, Guid? LeaseId = null);

public sealed record EdgeAgentPipeResponse(
    int SchemaVersion,
    bool Succeeded,
    EdgeAgentStatus? Status,
    string? DiagnosticCode,
    Guid? LeaseId = null,
    Guid? BackupId = null,
    string? DatabasePathHash = null,
    Guid? MaintenanceLeaseId = null);
