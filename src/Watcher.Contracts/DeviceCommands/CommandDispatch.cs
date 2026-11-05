using System.Text.Json.Serialization;

namespace Watcher.Contracts.DeviceCommands;

[JsonConverter(typeof(JsonStringEnumConverter<DeviceCommandAction>))]
public enum DeviceCommandAction
{
    [JsonStringEnumMemberName("start")]
    Start,
    [JsonStringEnumMemberName("stop")]
    Stop
}

public sealed record CommandDispatch(
    [property: JsonPropertyName("schemaVersion")]
    int SchemaVersion,
    [property: JsonPropertyName("commandId")]
    Guid CommandId,
    [property: JsonPropertyName("deviceId")]
    Guid DeviceId,
    [property: JsonPropertyName("locationId")]
    Guid LocationId,
    [property: JsonPropertyName("sequence")]
    long Sequence,
    [property: JsonPropertyName("action")]
    DeviceCommandAction Action,
    [property: JsonPropertyName("presetCode")]
    string? PresetCode,
    [property: JsonPropertyName("manifestHash")]
    string? ManifestHash,
    [property: JsonPropertyName("issuedAtUtc")]
    DateTimeOffset IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")]
    DateTimeOffset ExpiresAtUtc,
    [property: JsonPropertyName("maxPlaybackDurationSeconds")]
    int MaxPlaybackDurationSeconds,
    [property: JsonPropertyName("targetCommandId")]
    Guid? TargetCommandId);
