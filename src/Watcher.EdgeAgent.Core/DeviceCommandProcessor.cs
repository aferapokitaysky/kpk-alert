using System.Security.Cryptography;
using System.Text.Json;
using Watcher.Contracts.DeviceCommands;

namespace Watcher.EdgeAgent.Core;

public sealed class DeviceCommandProcessor(
    IEdgeAgentStore store,
    IAudioAssetResolver assets,
    IAudioPlayer audioPlayer,
    IEdgeAgentClock clock,
    TimeSpan maximumClockSkew,
    int maximumReceiptQueue,
    string? audioEndpointId = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _processingGate = new(1, 1);

    public async Task<CommandProcessingOutcome> ProcessAsync(
        CommandDispatch command,
        DeviceIdentity identity,
        CancellationToken cancellationToken)
    {
        await _processingGate.WaitAsync(cancellationToken);
        try
        {
            return await ProcessCoreAsync(command, identity, cancellationToken);
        }
        finally
        {
            _processingGate.Release();
        }
    }

    private async Task<CommandProcessingOutcome> ProcessCoreAsync(
        CommandDispatch command,
        DeviceIdentity identity,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(identity);

        if (command.DeviceId != identity.DeviceId || command.LocationId != identity.LocationId)
        {
            return new CommandProcessingOutcome(false, false, true, "wrong_device", null);
        }

        var payloadHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(command, JsonOptions)));
        ProcessedCommand? existing = await store.FindCommandAsync(command.CommandId, cancellationToken);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(existing.PayloadHash),
                    Convert.FromHexString(payloadHash)))
            {
                return new CommandProcessingOutcome(false, false, true, "command_id_conflict", existing.State);
            }

            if (command.Action == DeviceCommandAction.Stop && existing.State == DeviceCommandState.Accepted)
            {
                return await TryStopAsync(command, duplicate: true, cancellationToken);
            }

            await store.RequeueLatestReceiptAsync(command.CommandId, cancellationToken);
            return new CommandProcessingOutcome(true, true, false, existing.DiagnosticCode, existing.State);
        }

        var validationError = DeviceCommandValidator.Validate(command);
        if (validationError is not null)
        {
            return await RejectAsync(command, payloadHash, validationError, cancellationToken);
        }

        DateTimeOffset nowUtc = clock.UtcNow;
        if (command.IssuedAtUtc > nowUtc + maximumClockSkew)
        {
            return await RejectAsync(command, payloadHash, "clock_skew", cancellationToken);
        }

        if (nowUtc >= command.ExpiresAtUtc)
        {
            return await RejectAsync(command, payloadHash, "command_expired", cancellationToken,
                DeviceCommandState.Expired);
        }

        if (command.Sequence <= await store.GetLastSequenceAsync(identity.LocationId, cancellationToken))
        {
            return await RejectAsync(command, payloadHash, "stale_sequence", cancellationToken);
        }

        LocalAudioAsset? asset = null;
        if (command.Action == DeviceCommandAction.Start)
        {
            if (audioPlayer.ActiveCommandId is not null)
            {
                return await RejectAsync(command, payloadHash, "audio_output_busy", cancellationToken);
            }

            asset = await assets.ResolveAsync(identity.LocationId, command.PresetCode!, command.ManifestHash!, cancellationToken);
            if (asset is null)
            {
                return await RejectAsync(command, payloadHash, "preset_missing", cancellationToken);
            }

            if (!string.Equals(asset.PresetCode, command.PresetCode, StringComparison.Ordinal) ||
                !string.Equals(asset.ManifestHash, command.ManifestHash, StringComparison.OrdinalIgnoreCase))
            {
                return await RejectAsync(command, payloadHash, "manifest_mismatch", cancellationToken);
            }
        }

        if (await store.GetPendingReceiptCountAsync(cancellationToken) >= maximumReceiptQueue)
        {
            return new CommandProcessingOutcome(false, false, false, "receipt_queue_full", null);
        }

        AcceptCommandResult acceptResult = await store.TryAcceptAsync(command, payloadHash, nowUtc, cancellationToken);
        if (acceptResult == AcceptCommandResult.Duplicate)
        {
            await store.RequeueLatestReceiptAsync(command.CommandId, cancellationToken);
            ProcessedCommand? duplicate = await store.FindCommandAsync(command.CommandId, cancellationToken);
            return new CommandProcessingOutcome(true, true, false, duplicate?.DiagnosticCode, duplicate?.State);
        }

        var rejection = acceptResult switch
        {
            AcceptCommandResult.DuplicateIdConflict => "command_id_conflict",
            AcceptCommandResult.StaleSequence => "stale_sequence",
            AcceptCommandResult.QueueFull => "receipt_queue_full",
            _ => null
        };
        if (rejection is not null)
        {
            if (rejection == "command_id_conflict")
            {
                return new CommandProcessingOutcome(false, false, true, rejection, null);
            }

            return await RejectAsync(command, payloadHash, rejection, cancellationToken);
        }

        if (command.Action == DeviceCommandAction.Stop)
        {
            return await TryStopAsync(command, duplicate: false, cancellationToken);
        }

        DateTimeOffset deadlineUtc = command.IssuedAtUtc.AddSeconds(command.MaxPlaybackDurationSeconds);
        if (clock.UtcNow >= deadlineUtc)
        {
            await store.TransitionCommandAsync(command.CommandId, DeviceCommandState.Expired,
                "command_expired", clock.UtcNow, cancellationToken);
            return new CommandProcessingOutcome(false, false, false, "command_expired", DeviceCommandState.Expired);
        }

        try
        {
            await audioPlayer.StartAsync(
                new PlaybackRequest(command.CommandId, command.PresetCode!, asset!.FilePath, deadlineUtc, audioEndpointId),
                cancellationToken);
        }
        catch (AudioPlaybackException exception)
        {
            await store.TransitionCommandAsync(command.CommandId, DeviceCommandState.Failed,
                exception.DiagnosticCode, clock.UtcNow, cancellationToken);
            return new CommandProcessingOutcome(false, false, false, exception.DiagnosticCode, DeviceCommandState.Failed);
        }

        try
        {
            await store.TransitionCommandAsync(command.CommandId, DeviceCommandState.Playing,
                null, clock.UtcNow, cancellationToken);
            await audioPlayer.ConfirmStartedAsync(command.CommandId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await audioPlayer.AbortStartAsync(command.CommandId, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            await audioPlayer.AbortStartAsync(command.CommandId, CancellationToken.None);
            return new CommandProcessingOutcome(false, false, true,
                "command_state_persistence_failed", DeviceCommandState.Failed);
        }

        return new CommandProcessingOutcome(true, false, false, null, DeviceCommandState.Playing);
    }

    private async Task<CommandProcessingOutcome> TryStopAsync(
        CommandDispatch command,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        try
        {
            Guid targetCommandId = command.TargetCommandId!.Value;
            var stopped = await audioPlayer.StopAsync(targetCommandId, cancellationToken);
            if (stopped)
            {
                return await PersistStopOutcomeAsync(command, DeviceCommandState.Stopped, null,
                    duplicate, cancellationToken);
            }

            CommandProcessingOutcome? reconciled = await ReconcileTerminalTargetAsync(
                command, duplicate, cancellationToken);
            if (reconciled is not null)
            {
                return reconciled;
            }

            return RetryStop(duplicate, "stop_target_pending");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            try
            {
                CommandProcessingOutcome? reconciled = await ReconcileTerminalTargetAsync(
                    command, duplicate, cancellationToken);
                return reconciled ?? RetryStop(duplicate, "stop_attempt_failed");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Без успішної перевірки цілі не завершуємо stop і залишаємо доставку для повтору.
                return RetryStop(duplicate, "stop_attempt_failed");
            }
        }
    }

    private async Task<CommandProcessingOutcome?> ReconcileTerminalTargetAsync(
        CommandDispatch command,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        ProcessedCommand? target = await store.FindCommandAsync(command.TargetCommandId!.Value, cancellationToken);
        if (target is not null && target.State is DeviceCommandState.Accepted or DeviceCommandState.Playing)
        {
            return null;
        }

        // STOP може випередити START. Фіксація більшого sequence відхилить запізнілий запуск.
        return await PersistStopOutcomeAsync(command, DeviceCommandState.Stopped,
            null, duplicate, cancellationToken);
    }

    private async Task<CommandProcessingOutcome> PersistStopOutcomeAsync(
        CommandDispatch command,
        DeviceCommandState state,
        string? diagnosticCode,
        bool duplicate,
        CancellationToken cancellationToken)
    {
        await store.TransitionCommandAsync(command.CommandId, state, diagnosticCode,
            clock.UtcNow, cancellationToken);
        return new CommandProcessingOutcome(true, duplicate, false, diagnosticCode, state);
    }

    private static CommandProcessingOutcome RetryStop(
        bool duplicate,
        string diagnosticCode) =>
        new(false, duplicate, true, diagnosticCode, DeviceCommandState.Accepted);

    private async Task<CommandProcessingOutcome> RejectAsync(
        CommandDispatch command,
        string payloadHash,
        string diagnosticCode,
        CancellationToken cancellationToken,
        DeviceCommandState state = DeviceCommandState.Rejected)
    {
        if (command.CommandId != Guid.Empty && command.Sequence > 0 &&
            await store.GetPendingReceiptCountAsync(cancellationToken) < maximumReceiptQueue)
        {
            var persisted = await store.RecordRejectedCommandAsync(
                command, payloadHash, state, diagnosticCode, clock.UtcNow, cancellationToken);
            if (!persisted)
            {
                ProcessedCommand? existing = await store.FindCommandAsync(command.CommandId, cancellationToken);
                if (existing is not null)
                {
                    if (!CryptographicOperations.FixedTimeEquals(
                            Convert.FromHexString(existing.PayloadHash), Convert.FromHexString(payloadHash)))
                    {
                        return new CommandProcessingOutcome(false, false, true, "command_id_conflict", existing.State);
                    }

                    await store.RequeueLatestReceiptAsync(command.CommandId, cancellationToken);
                    return new CommandProcessingOutcome(true, true, false, existing.DiagnosticCode, existing.State);
                }
            }
        }

        return new CommandProcessingOutcome(false, false,
            diagnosticCode == "invalid_identity", diagnosticCode, state);
    }

}

public sealed record CommandProcessingOutcome(
    bool Accepted,
    bool Duplicate,
    bool CloseConnection,
    string? DiagnosticCode,
    DeviceCommandState? State);

public sealed class AudioPlaybackException(string diagnosticCode, Exception innerException)
    : Exception("Аудіопотік не вдалося запустити.", innerException)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}
