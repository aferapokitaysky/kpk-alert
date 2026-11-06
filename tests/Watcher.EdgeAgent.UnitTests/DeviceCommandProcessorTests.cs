using System.Security.Cryptography;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Persistence;

namespace Watcher.EdgeAgent.UnitTests;

public sealed class DeviceCommandProcessorTests : IAsyncLifetime
{
    private static readonly Guid DeviceId = Guid.Parse("0844f185-07c3-421c-8e15-d71bd3e4d67a");
    private static readonly Guid LocationId = Guid.Parse("f3d2797b-0b05-4db5-9cea-26ea46fd8263");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kgta-edge-tests", Guid.NewGuid().ToString("N"));
    private SqliteEdgeAgentStore _store = null!;
    private FakeAudioPlayer _player = null!;
    private FakeAssetResolver _assets = null!;
    private FakeClock _clock = null!;
    private DeviceCommandProcessor _processor = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteEdgeAgentStore(Path.Combine(_directory, "agent.db"), 20);
        await _store.InitializeAsync(CancellationToken.None);
        _player = new FakeAudioPlayer();
        _assets = new FakeAssetResolver();
        _clock = new FakeClock(DateTimeOffset.Parse("2026-11-05T09:00:00Z"));
        _processor = new DeviceCommandProcessor(
            _store, _assets, _player, _clock, TimeSpan.FromSeconds(120), 20, "endpoint-1");
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ValidStart_IsDurablyAcceptedBeforePlayback_AndQueuesPlayingReceipt()
    {
        CommandDispatch command = CreateCommand(1);

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            command, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal(DeviceCommandState.Playing, result.State);
        Assert.Equal(command.CommandId, _player.ActiveCommandId);
        Assert.Equal("endpoint-1", _player.LastRequest?.AudioEndpointId);
        ProcessedCommand stored = Assert.IsType<ProcessedCommand>(
            await _store.FindCommandAsync(command.CommandId, CancellationToken.None));
        Assert.Equal(DeviceCommandState.Playing, stored.State);
        Assert.Equal(DeviceCommandState.Accepted, (await _store.GetNextReceiptAsync(_clock.UtcNow, default))!.State);
    }

    [Fact]
    public async Task ExactRedelivery_RequeuesLastStableReceipt_WithoutStartingAudioAgain()
    {
        CommandDispatch command = CreateCommand(1);
        await _processor.ProcessAsync(command, new DeviceIdentity(DeviceId, LocationId), default);
        PendingCommandReceipt accepted = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));
        await _store.MarkReceiptDeliveredAsync(accepted.EventId, default);
        PendingCommandReceipt original = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            command, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.True(result.Duplicate);
        Assert.Equal(1, _player.StartCount);
        PendingCommandReceipt requeued = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));
        Assert.Equal(original.EventId, requeued.EventId);
        Assert.Equal(original.State, requeued.State);
    }

    [Fact]
    public async Task SameCommandIdWithChangedPayload_IsRejectedWithoutPlayback()
    {
        CommandDispatch original = CreateCommand(1);
        await _processor.ProcessAsync(original, new DeviceIdentity(DeviceId, LocationId), default);
        CommandDispatch altered = original with { PresetCode = "evacuation" };

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            altered, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.True(result.CloseConnection);
        Assert.Equal("command_id_conflict", result.DiagnosticCode);
        Assert.Equal(1, _player.StartCount);
    }

    [Fact]
    public async Task IdentityMismatch_IsRejectedBeforePlayback()
    {
        CommandProcessingOutcome result = await _processor.ProcessAsync(
            CreateCommand(1), new DeviceIdentity(Guid.NewGuid(), LocationId), CancellationToken.None);

        Assert.Equal("wrong_device", result.DiagnosticCode);
        Assert.Null(_player.ActiveCommandId);
        Assert.Null(await _store.GetNextReceiptAsync(_clock.UtcNow, default));
    }

    [Fact]
    public async Task ExpiredCommand_IsNotPlayed_AndQueuesExpiredReceipt()
    {
        CommandDispatch command = CreateCommand(1) with
        {
            IssuedAtUtc = _clock.UtcNow.AddMinutes(-10),
            ExpiresAtUtc = _clock.UtcNow.AddSeconds(-1)
        };

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            command, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal(DeviceCommandState.Expired, result.State);
        Assert.Equal("command_expired", result.DiagnosticCode);
        Assert.Null(_player.ActiveCommandId);
    }

    [Fact]
    public async Task FullReceiptQueue_ReportsPressureWithoutAcceptingCommandOrDiscardingReceipts()
    {
        for (var sequence = 1; sequence <= 20; sequence++)
        {
            await _store.AddReceiptAsync(Guid.NewGuid(), sequence, DeviceCommandState.Rejected,
                _clock.UtcNow, "preset_missing", CancellationToken.None);
        }

        CommandDispatch command = CreateCommand(1);
        CommandProcessingOutcome result = await _processor.ProcessAsync(
            command, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal("receipt_queue_full", result.DiagnosticCode);
        Assert.False(result.Accepted);
        Assert.Null(await _store.FindCommandAsync(command.CommandId, CancellationToken.None));
        Assert.Equal(20, await _store.GetPendingReceiptCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FutureIssuedCommandBeyondClockSkew_IsRejected()
    {
        CommandDispatch command = CreateCommand(1) with
        {
            IssuedAtUtc = _clock.UtcNow.AddMinutes(3),
            ExpiresAtUtc = _clock.UtcNow.AddMinutes(8)
        };

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            command, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal("clock_skew", result.DiagnosticCode);
        Assert.Null(_player.ActiveCommandId);
    }

    [Fact]
    public async Task StartWhilePlaybackActive_IsRejectedWithoutReplacingActiveCommand()
    {
        CommandDispatch active = CreateCommand(1);
        await _processor.ProcessAsync(active, new DeviceIdentity(DeviceId, LocationId), default);
        CommandDispatch next = CreateCommand(2);

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            next, new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal("audio_output_busy", result.DiagnosticCode);
        Assert.Equal(active.CommandId, _player.ActiveCommandId);
        Assert.Equal(1, _player.StartCount);
    }

    [Fact]
    public async Task MissingLocalPreset_IsRejected()
    {
        _assets.Asset = null;

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            CreateCommand(1), new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal("preset_missing", result.DiagnosticCode);
        Assert.Equal(0, _player.StartCount);
    }

    [Fact]
    public async Task StaleSequence_IsRejectedByPersistentStore()
    {
        await _processor.ProcessAsync(CreateCommand(8), new DeviceIdentity(DeviceId, LocationId), default);

        CommandProcessingOutcome result = await _processor.ProcessAsync(
            CreateCommand(7), new DeviceIdentity(DeviceId, LocationId), CancellationToken.None);

        Assert.Equal("stale_sequence", result.DiagnosticCode);
        Assert.Equal(1, _player.StartCount);
    }

    [Fact]
    public async Task InterruptedPlaybackRecovery_MarksFailedAndDoesNotReplay()
    {
        CommandDispatch command = CreateCommand(1);
        await _processor.ProcessAsync(command, new DeviceIdentity(DeviceId, LocationId), default);
        IReadOnlyList<PendingCommandReceipt> recovered = await _store.RecoverInterruptedPlaybackAsync(
            _clock.UtcNow.AddSeconds(3), CancellationToken.None);

        Assert.Single(recovered);
        Assert.Equal("agent_restart", recovered[0].DiagnosticCode);
        Assert.Equal(DeviceCommandState.Failed,
            (await _store.FindCommandAsync(command.CommandId, default))!.State);
        Assert.Equal(1, _player.StartCount);
    }

    [Fact]
    public async Task AcceptedStop_RetriesAfterStopFailureAndRecoveryBeforeTerminalReceipt()
    {
        CommandDispatch start = CreateCommand(1);
        await _processor.ProcessAsync(start, new DeviceIdentity(DeviceId, LocationId), default);
        for (var index = 0; index < 2; index++)
        {
            PendingCommandReceipt receipt = Assert.IsType<PendingCommandReceipt>(
                await _store.GetNextReceiptAsync(_clock.UtcNow, default));
            await _store.MarkReceiptDeliveredAsync(receipt.EventId, default);
        }

        CommandDispatch stop = CreateStopCommand(2, start.CommandId);
        _player.StopFailuresRemaining = 1;

        CommandProcessingOutcome firstAttempt = await _processor.ProcessAsync(
            stop, new DeviceIdentity(DeviceId, LocationId), default);

        Assert.True(firstAttempt.CloseConnection);
        Assert.Equal("stop_attempt_failed", firstAttempt.DiagnosticCode);
        Assert.Equal(DeviceCommandState.Accepted, firstAttempt.State);
        Assert.Equal(DeviceCommandState.Accepted,
            (await _store.FindCommandAsync(stop.CommandId, default))!.State);
        Assert.Null(await _store.GetNextReceiptAsync(_clock.UtcNow, default));

        IReadOnlyList<PendingCommandReceipt> recovery = await _store.RecoverInterruptedPlaybackAsync(
            _clock.UtcNow.AddSeconds(1), default);

        PendingCommandReceipt recoveredStartReceipt = Assert.Single(recovery);
        Assert.Equal(start.CommandId, recoveredStartReceipt.CommandId);
        Assert.Equal(DeviceCommandState.Failed, recoveredStartReceipt.State);
        Assert.Equal("agent_restart", recoveredStartReceipt.DiagnosticCode);
        Assert.Equal(DeviceCommandState.Accepted,
            (await _store.FindCommandAsync(stop.CommandId, default))!.State);
        PendingCommandReceipt queuedRecoveryReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow.AddSeconds(2), default));
        Assert.Equal(start.CommandId, queuedRecoveryReceipt.CommandId);
        await _store.MarkReceiptDeliveredAsync(queuedRecoveryReceipt.EventId, default);

        CommandProcessingOutcome retry = await _processor.ProcessAsync(
            stop, new DeviceIdentity(DeviceId, LocationId), default);

        Assert.True(retry.Duplicate);
        Assert.Equal(DeviceCommandState.Stopped, retry.State);
        Assert.Equal(2, _player.StopCount);
        PendingCommandReceipt terminalReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));
        Assert.Equal(stop.CommandId, terminalReceipt.CommandId);
        Assert.Equal(DeviceCommandState.Stopped, terminalReceipt.State);
    }

    [Fact]
    public async Task StopArrivingBeforeTargetStart_AdvancesSequenceAndRejectsDelayedStart()
    {
        var targetCommandId = Guid.NewGuid();
        CommandDispatch stop = CreateStopCommand(2, targetCommandId);

        CommandProcessingOutcome stopped = await _processor.ProcessAsync(
            stop, new DeviceIdentity(DeviceId, LocationId), default);

        Assert.False(stopped.CloseConnection);
        Assert.Equal(DeviceCommandState.Stopped, stopped.State);
        Assert.Equal(DeviceCommandState.Stopped,
            (await _store.FindCommandAsync(stop.CommandId, default))!.State);
        Assert.Equal(2, await _store.GetLastSequenceAsync(LocationId, default));
        PendingCommandReceipt stopReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));
        Assert.Equal(stop.CommandId, stopReceipt.CommandId);
        Assert.Equal(DeviceCommandState.Stopped, stopReceipt.State);

        CommandDispatch start = CreateCommand(1, targetCommandId);
        CommandProcessingOutcome delayedStart = await _processor.ProcessAsync(
            start, new DeviceIdentity(DeviceId, LocationId), default);
        Assert.Equal(DeviceCommandState.Rejected, delayedStart.State);
        Assert.Equal("stale_sequence", delayedStart.DiagnosticCode);
        Assert.Null(_player.ActiveCommandId);
        Assert.Equal(0, _player.StartCount);
    }

    [Fact]
    public async Task StopAfterNaturalCompletion_IsAcknowledgedAsSafeNoOp()
    {
        CommandDispatch start = CreateCommand(1);
        await _processor.ProcessAsync(start, new DeviceIdentity(DeviceId, LocationId), default);
        for (var index = 0; index < 2; index++)
        {
            PendingCommandReceipt receipt = Assert.IsType<PendingCommandReceipt>(
                await _store.GetNextReceiptAsync(_clock.UtcNow, default));
            await _store.MarkReceiptDeliveredAsync(receipt.EventId, default);
        }

        await _store.TransitionCommandAsync(start.CommandId, DeviceCommandState.Completed,
            null, _clock.UtcNow, default);
        PendingCommandReceipt completedReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));
        await _store.MarkReceiptDeliveredAsync(completedReceipt.EventId, default);
        _player.ClearActivePlayback();

        CommandDispatch stop = CreateStopCommand(2, start.CommandId);
        CommandProcessingOutcome outcome = await _processor.ProcessAsync(
            stop, new DeviceIdentity(DeviceId, LocationId), default);

        Assert.Equal(DeviceCommandState.Stopped, outcome.State);
        Assert.Null(outcome.DiagnosticCode);
        Assert.Equal(DeviceCommandState.Stopped,
            (await _store.FindCommandAsync(stop.CommandId, default))!.State);
        PendingCommandReceipt stopReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(_clock.UtcNow, default));
        Assert.Equal(stop.CommandId, stopReceipt.CommandId);
        Assert.Equal(DeviceCommandState.Stopped, stopReceipt.State);
    }

    [Fact]
    public async Task PlayingStatePersistenceFailure_AbortsPlaybackAndReleasesAudioSession()
    {
        DeviceCommandProcessor processor = new(
            new PlayingTransitionFailingStore(_store),
            _assets,
            _player,
            _clock,
            TimeSpan.FromSeconds(120),
            20,
            "endpoint-1");
        CommandDispatch command = CreateCommand(1);

        CommandProcessingOutcome outcome = await processor.ProcessAsync(
            command, new DeviceIdentity(DeviceId, LocationId), default);

        Assert.True(outcome.CloseConnection);
        Assert.Equal("command_state_persistence_failed", outcome.DiagnosticCode);
        Assert.Equal(DeviceCommandState.Failed, outcome.State);
        Assert.Equal(command.CommandId, _player.LastRequest?.CommandId);
        Assert.Equal(1, _player.AbortStartCount);
        Assert.Null(_player.ActiveCommandId);
    }

    private CommandDispatch CreateCommand(long sequence, Guid? commandId = null) => new(
        DeviceCommandValidator.CurrentSchemaVersion,
        commandId ?? Guid.NewGuid(),
        DeviceId,
        LocationId,
        sequence,
        DeviceCommandAction.Start,
        "air_raid",
        new string('a', 64),
        _clock.UtcNow,
        _clock.UtcNow.AddMinutes(5),
        900,
        null);

    private CommandDispatch CreateStopCommand(long sequence, Guid targetCommandId) => new(
        DeviceCommandValidator.CurrentSchemaVersion,
        Guid.NewGuid(),
        DeviceId,
        LocationId,
        sequence,
        DeviceCommandAction.Stop,
        null,
        null,
        _clock.UtcNow,
        _clock.UtcNow.AddMinutes(5),
        0,
        targetCommandId);

    private sealed class FakeAssetResolver : IAudioAssetResolver
    {
        public LocalAudioAsset? Asset { get; set; } = new("air_raid", new string('a', 64), "test.wav", "hash");

        public Task<LocalAudioAsset?> ResolveAsync(Guid locationId, string presetCode, string manifestHash,
            CancellationToken cancellationToken) => Task.FromResult(Asset);
    }

    private sealed class FakeAudioPlayer : IAudioPlayer
    {
        public Guid? ActiveCommandId { get; private set; }
        public PlaybackRequest? LastRequest { get; private set; }
        public int StartCount { get; private set; }
        public int StopCount { get; private set; }
        public int StopFailuresRemaining { get; set; }
        public int AbortStartCount { get; private set; }

        public void ClearActivePlayback() => ActiveCommandId = null;

        public Task StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
        {
            StartCount++;
            ActiveCommandId = request.CommandId;
            LastRequest = request;
            return Task.CompletedTask;
        }

        public Task<bool> StopAsync(Guid targetCommandId, CancellationToken cancellationToken)
        {
            StopCount++;
            if (StopFailuresRemaining > 0)
            {
                StopFailuresRemaining--;
                throw new IOException("Temporary audio stop failure.");
            }

            var stopped = ActiveCommandId == targetCommandId;
            if (stopped)
            {
                ActiveCommandId = null;
            }

            return Task.FromResult(stopped);
        }

        public Task AbortStartAsync(Guid commandId, CancellationToken cancellationToken)
        {
            AbortStartCount++;
            if (ActiveCommandId == commandId)
            {
                ActiveCommandId = null;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class PlayingTransitionFailingStore(IEdgeAgentStore inner) : IEdgeAgentStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => inner.InitializeAsync(cancellationToken);
        public Task<IReadOnlyList<PendingCommandReceipt>> RecoverInterruptedPlaybackAsync(
            DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            inner.RecoverInterruptedPlaybackAsync(nowUtc, cancellationToken);
        public Task<ProcessedCommand?> FindCommandAsync(Guid commandId, CancellationToken cancellationToken) =>
            inner.FindCommandAsync(commandId, cancellationToken);
        public Task<long> GetLastSequenceAsync(Guid locationId, CancellationToken cancellationToken) =>
            inner.GetLastSequenceAsync(locationId, cancellationToken);
        public Task RequeueLatestReceiptAsync(Guid commandId, CancellationToken cancellationToken) =>
            inner.RequeueLatestReceiptAsync(commandId, cancellationToken);
        public Task<AcceptCommandResult> TryAcceptAsync(
            CommandDispatch command, string payloadHash, DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            inner.TryAcceptAsync(command, payloadHash, nowUtc, cancellationToken);
        public Task<bool> RecordRejectedCommandAsync(
            CommandDispatch command, string payloadHash, DeviceCommandState state, string diagnosticCode,
            DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            inner.RecordRejectedCommandAsync(command, payloadHash, state, diagnosticCode, nowUtc, cancellationToken);
        public Task<PendingCommandReceipt> AddReceiptAsync(
            Guid commandId, long sequence, DeviceCommandState state, DateTimeOffset occurredAtUtc,
            string? diagnosticCode, CancellationToken cancellationToken) =>
            inner.AddReceiptAsync(commandId, sequence, state, occurredAtUtc, diagnosticCode, cancellationToken);
        public Task UpdateCommandStateAsync(
            Guid commandId, DeviceCommandState state, string? diagnosticCode, CancellationToken cancellationToken) =>
            inner.UpdateCommandStateAsync(commandId, state, diagnosticCode, cancellationToken);
        public Task TransitionCommandAsync(
            Guid commandId, DeviceCommandState state, string? diagnosticCode, DateTimeOffset occurredAtUtc,
            CancellationToken cancellationToken) => state == DeviceCommandState.Playing
                ? Task.FromException(new IOException("Simulated SQLite transition failure."))
                : inner.TransitionCommandAsync(commandId, state, diagnosticCode, occurredAtUtc, cancellationToken);
        public Task<int> GetPendingReceiptCountAsync(CancellationToken cancellationToken) =>
            inner.GetPendingReceiptCountAsync(cancellationToken);
        public Task<PendingCommandReceipt?> GetNextReceiptAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) =>
            inner.GetNextReceiptAsync(nowUtc, cancellationToken);
        public Task MarkReceiptDeliveredAsync(Guid eventId, CancellationToken cancellationToken) =>
            inner.MarkReceiptDeliveredAsync(eventId, cancellationToken);
        public Task ScheduleReceiptRetryAsync(
            Guid eventId, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) =>
            inner.ScheduleReceiptRetryAsync(eventId, nextAttemptAtUtc, cancellationToken);
    }

    private sealed class FakeClock(DateTimeOffset utcNow) : IEdgeAgentClock
    {
        public DateTimeOffset UtcNow { get; private set; } = utcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
