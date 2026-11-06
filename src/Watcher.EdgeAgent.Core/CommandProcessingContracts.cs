using Watcher.Contracts.DeviceCommands;

namespace Watcher.EdgeAgent.Core;

public sealed record DeviceIdentity(Guid DeviceId, Guid LocationId);

public sealed record LocalAudioAsset(string PresetCode, string ManifestHash, string FilePath, string Sha256);

public sealed record PlaybackRequest(
    Guid CommandId,
    string PresetCode,
    string FilePath,
    DateTimeOffset DeadlineUtc,
    string? AudioEndpointId);

public sealed record ProcessedCommand(
    Guid CommandId,
    long Sequence,
    string PayloadHash,
    DeviceCommandState State,
    string? DiagnosticCode);

public sealed record PendingCommandReceipt(
    Guid EventId,
    Guid CommandId,
    long Sequence,
    DeviceCommandState State,
    DateTimeOffset OccurredAtUtc,
    string? DiagnosticCode,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc);

public enum AcceptCommandResult
{
    Accepted,
    Duplicate,
    DuplicateIdConflict,
    StaleSequence,
    QueueFull
}

public interface IEdgeAgentStore
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<PendingCommandReceipt>> RecoverInterruptedPlaybackAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<ProcessedCommand?> FindCommandAsync(Guid commandId, CancellationToken cancellationToken);
    Task<long> GetLastSequenceAsync(Guid locationId, CancellationToken cancellationToken) => Task.FromResult(0L);
    Task RequeueLatestReceiptAsync(Guid commandId, CancellationToken cancellationToken);
    Task<AcceptCommandResult> TryAcceptAsync(
        CommandDispatch command,
        string payloadHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<bool> RecordRejectedCommandAsync(
        CommandDispatch command,
        string payloadHash,
        DeviceCommandState state,
        string diagnosticCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken);
    Task<PendingCommandReceipt> AddReceiptAsync(
        Guid commandId,
        long sequence,
        DeviceCommandState state,
        DateTimeOffset occurredAtUtc,
        string? diagnosticCode,
        CancellationToken cancellationToken);
    Task UpdateCommandStateAsync(
        Guid commandId,
        DeviceCommandState state,
        string? diagnosticCode,
        CancellationToken cancellationToken);
    Task TransitionCommandAsync(
        Guid commandId,
        DeviceCommandState state,
        string? diagnosticCode,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken);
    Task<int> GetPendingReceiptCountAsync(CancellationToken cancellationToken);
    Task<PendingCommandReceipt?> GetNextReceiptAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken);
    Task MarkReceiptDeliveredAsync(Guid eventId, CancellationToken cancellationToken);
    Task ScheduleReceiptRetryAsync(
        Guid eventId,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken);
}

public interface IAudioAssetResolver
{
    Task<LocalAudioAsset?> ResolveAsync(
        Guid locationId,
        string presetCode,
        string manifestHash,
        CancellationToken cancellationToken);
}

public interface IAudioPlayer
{
    Guid? ActiveCommandId { get; }
    Task StartAsync(PlaybackRequest request, CancellationToken cancellationToken);
    Task ConfirmStartedAsync(Guid commandId, CancellationToken cancellationToken) => Task.CompletedTask;
    Task AbortStartAsync(Guid commandId, CancellationToken cancellationToken) => Task.CompletedTask;
    Task<bool> StopAsync(Guid targetCommandId, CancellationToken cancellationToken);
}

public interface IAudioEndpointAvailabilityMonitor
{
    bool IsAvailable(string? endpointId);
}

public interface IAudioPlaybackEventHandler
{
    Task OnPlaybackFinishedAsync(
        Guid commandId,
        DeviceCommandState state,
        string? diagnosticCode,
        CancellationToken cancellationToken);
}

public interface IEdgeAgentClock
{
    DateTimeOffset UtcNow { get; }
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp);
}
