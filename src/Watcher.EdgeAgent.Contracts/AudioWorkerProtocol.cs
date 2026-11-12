using Watcher.Contracts.DeviceCommands;

namespace Watcher.EdgeAgent.Contracts;

public sealed record AudioWorkerRequest(
    int SchemaVersion,
    Guid RequestId,
    string Operation,
    Guid? CommandId = null,
    Guid? EpisodeAnchorCommandId = null,
    long? Sequence = null,
    string? PayloadHash = null,
    string? PresetCode = null,
    string? AudioFileRelativePath = null,
    string? Sha256 = null,
    DateTimeOffset? DeadlineUtc = null,
    string? AudioEndpointId = null,
    AudioTestSound? TestSound = null);

public sealed record AudioWorkerPlaybackEvent(
    Guid CommandId,
    DeviceCommandState State,
    string? DiagnosticCode);

public sealed record AudioWorkerResponse(
    int SchemaVersion,
    Guid RequestId,
    bool Succeeded,
    string? DiagnosticCode = null,
    IReadOnlyList<EdgeAgentAudioEndpoint>? AudioEndpoints = null,
    Guid? ActiveCommandId = null,
    Guid? ActiveEpisodeAnchorCommandId = null,
    AudioWorkerPlaybackEvent? PlaybackEvent = null);
