using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Persistence;

namespace Watcher.EdgeAgent.UnitTests;

public sealed class SqliteEdgeAgentStoreTests : IAsyncLifetime
{
    private static readonly Guid LocationId = Guid.Parse("f3d2797b-0b05-4db5-9cea-26ea46fd8263");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kgta-edge-store-tests", Guid.NewGuid().ToString("N"));
    private SqliteEdgeAgentStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteEdgeAgentStore(Path.Combine(_directory, "agent.db"), 10);
        await _store.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task TryAccept_PersistsAcceptedReceiptAtomically_AndRejectsOldSequence()
    {
        CommandDispatch command = CreateCommand(Guid.NewGuid(), 4);
        string payload = new('b', 64);

        Assert.Equal(AcceptCommandResult.Accepted,
            await _store.TryAcceptAsync(command, payload, DateTimeOffset.UtcNow, default));
        PendingCommandReceipt acceptedReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));
        Assert.Equal(DeviceCommandState.Accepted, acceptedReceipt.State);
        Assert.Equal(AcceptCommandResult.Duplicate,
            await _store.TryAcceptAsync(command, payload, DateTimeOffset.UtcNow, default));
        Assert.Equal(AcceptCommandResult.StaleSequence,
            await _store.TryAcceptAsync(CreateCommand(Guid.NewGuid(), 3), new string('c', 64), DateTimeOffset.UtcNow, default));
    }

    [Fact]
    public async Task ReceiptRetry_PreservesEventIdAndPayload()
    {
        CommandDispatch command = CreateCommand(Guid.NewGuid(), 1);
        await _store.TryAcceptAsync(command, new string('d', 64), DateTimeOffset.UtcNow, default);
        PendingCommandReceipt original = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));
        await _store.ScheduleReceiptRetryAsync(original.EventId, DateTimeOffset.UtcNow.AddMinutes(2), default);

        Assert.Null(await _store.GetNextReceiptAsync(DateTimeOffset.UtcNow.AddMinutes(1), default));
        await _store.ScheduleReceiptRetryAsync(original.EventId, DateTimeOffset.UtcNow, default);
        PendingCommandReceipt retry = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(DateTimeOffset.UtcNow.AddSeconds(1), default));

        Assert.Equal(original.EventId, retry.EventId);
        Assert.Equal(original.CommandId, retry.CommandId);
        Assert.Equal(original.State, retry.State);
        Assert.Equal(original.OccurredAtUtc, retry.OccurredAtUtc);
        Assert.Equal(2, retry.AttemptCount);
    }

    [Fact]
    public async Task RestartRecovery_IsIdempotentAndNeverReplaysPlayback()
    {
        CommandDispatch command = CreateCommand(Guid.NewGuid(), 1);
        await _store.TryAcceptAsync(command, new string('e', 64), DateTimeOffset.UtcNow, default);
        await _store.UpdateCommandStateAsync(command.CommandId, DeviceCommandState.Playing, null, default);

        IReadOnlyList<PendingCommandReceipt> firstRecovery = await _store.RecoverInterruptedPlaybackAsync(DateTimeOffset.UtcNow, default);
        IReadOnlyList<PendingCommandReceipt> secondRecovery = await _store.RecoverInterruptedPlaybackAsync(DateTimeOffset.UtcNow, default);

        Assert.Single(firstRecovery);
        Assert.Empty(secondRecovery);
        Assert.Equal(DeviceCommandState.Failed,
            (await _store.FindCommandAsync(command.CommandId, default))!.State);
        Assert.Equal(2, await _store.GetPendingReceiptCountAsync(default));
    }

    [Fact]
    public async Task RestartRecovery_LeavesAcceptedStopPendingForRedelivery()
    {
        CommandDispatch stop = CreateStopCommand(Guid.NewGuid(), 1, Guid.NewGuid());
        Assert.Equal(AcceptCommandResult.Accepted,
            await _store.TryAcceptAsync(stop, new string('a', 64), DateTimeOffset.UtcNow, default));

        IReadOnlyList<PendingCommandReceipt> recovered = await _store.RecoverInterruptedPlaybackAsync(
            DateTimeOffset.UtcNow.AddSeconds(1), default);

        Assert.Empty(recovered);
        ProcessedCommand stored = Assert.IsType<ProcessedCommand>(
            await _store.FindCommandAsync(stop.CommandId, default));
        Assert.Equal(DeviceCommandState.Accepted, stored.State);
        Assert.Null(await _store.GetNextReceiptAsync(DateTimeOffset.UtcNow.AddSeconds(2), default));
    }

    [Fact]
    public async Task QueueLimit_RejectsCommandBeforeSequenceAdvances()
    {
        await _store.DisposeAsync();
        _store = new SqliteEdgeAgentStore(Path.Combine(_directory, "limited.db"), 3);
        await _store.InitializeAsync(default);

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        for (var sequence = 1; sequence <= 3; sequence++)
        {
            Assert.True(await _store.RecordRejectedCommandAsync(
                CreateCommand(Guid.NewGuid(), sequence), new string('f', 64),
                DeviceCommandState.Rejected, "preset_missing", nowUtc, default));
        }
        AcceptCommandResult result = await _store.TryAcceptAsync(
            CreateCommand(Guid.NewGuid(), 4), new string('f', 64), nowUtc, default);

        Assert.Equal(AcceptCommandResult.QueueFull, result);
        Assert.Equal(3, await _store.GetPendingReceiptCountAsync(default));
    }

    private static CommandDispatch CreateCommand(Guid commandId, long sequence) => new(
        1, commandId, Guid.NewGuid(), LocationId, sequence, DeviceCommandAction.Start,
        "air_raid", new string('a', 64), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5), 900, null);

    private static CommandDispatch CreateStopCommand(Guid commandId, long sequence, Guid targetCommandId) => new(
        1, commandId, Guid.NewGuid(), LocationId, sequence, DeviceCommandAction.Stop,
        null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 0, targetCommandId);
}
