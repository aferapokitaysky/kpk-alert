using System.Text.Json.Serialization;

namespace Watcher.Contracts.DeviceCommands;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceCommandState>))]
public enum DeviceCommandState
{
    [JsonStringEnumMemberName("accepted")]
    Accepted,
    [JsonStringEnumMemberName("playing")]
    Playing,
    [JsonStringEnumMemberName("completed")]
    Completed,
    [JsonStringEnumMemberName("stopped")]
    Stopped,
    [JsonStringEnumMemberName("rejected")]
    Rejected,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("expired")]
    Expired
}

public sealed record CommandReceipt(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonPropertyName("eventId")]
    Guid EventId,
    [property: JsonPropertyName("commandId")]
    Guid CommandId,
    [property: JsonPropertyName("sequence")]
    long Sequence,
    [property: JsonPropertyName("state")]
    DeviceCommandState State,
    [property: JsonPropertyName("occurredAtUtc")]
    DateTimeOffset OccurredAtUtc,
    [property: JsonPropertyName("diagnosticCode")]
    string? DiagnosticCode);
