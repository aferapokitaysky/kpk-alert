using Microsoft.Data.Sqlite;
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
        var rejectedReceiptRecorded = await _store.RecordRejectedCommandAsync(
            CreateCommand(Guid.NewGuid(), 5), new string('f', 64), DeviceCommandState.Rejected,
            "queue_full", nowUtc, default);

        Assert.Equal(AcceptCommandResult.QueueFull, result);
        Assert.False(rejectedReceiptRecorded);
        Assert.Equal(3, await _store.GetPendingReceiptCountAsync(default));
    }

    [Fact]
    public async Task Initialize_IsIdempotentForAnOpenStore()
    {
        await _store.InitializeAsync(CancellationToken.None);

        Assert.Equal(0, await _store.GetPendingReceiptCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Initialize_RejectsReceiptQueueTooSmallForStartLifecycle()
    {
        await using SqliteEdgeAgentStore undersizedStore = new(
            Path.Combine(_directory, "undersized.db"), EdgeAgentOptions.StartCommandReceiptCapacity - 1);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => undersizedStore.InitializeAsync(CancellationToken.None));

        Assert.Contains("Черга квитанцій", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_RejectsNewerSchemaWithoutChangingDatabaseVersion()
    {
        var databasePath = Path.Combine(_directory, "future-schema.db");
        await using (SqliteConnection connection = CreateConnection(databasePath))
        {
            await connection.OpenAsync();
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            await command.ExecuteNonQueryAsync();
        }

        await using SqliteEdgeAgentStore futureStore = new(databasePath, 10);
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => futureStore.InitializeAsync(CancellationToken.None));

        Assert.Contains("новіша за підтримувану", exception.Message, StringComparison.Ordinal);
        await using SqliteConnection verification = CreateConnection(databasePath);
        await verification.OpenAsync();
        await using SqliteCommand versionQuery = verification.CreateCommand();
        versionQuery.CommandText = "PRAGMA user_version;";
        Assert.Equal(2L, await versionQuery.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Initialize_MigratesLegacyReceiptOutboxAndPreservesPendingReceipt()
    {
        var databasePath = Path.Combine(_directory, "legacy-outbox.db");
        var deliveredEventId = Guid.NewGuid();
        var pendingEventId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        DateTimeOffset occurredAtUtc = DateTimeOffset.UtcNow.AddMinutes(-2);
        DateTimeOffset nextAttemptAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await using (SqliteConnection connection = CreateConnection(databasePath))
        {
            await connection.OpenAsync();
            await using SqliteCommand create = connection.CreateCommand();
            create.CommandText = """
                CREATE TABLE receipt_outbox (
                    event_id TEXT NOT NULL UNIQUE,
                    command_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    diagnostic_code TEXT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at_utc TEXT NOT NULL,
                    delivered_at_utc TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();

            await InsertLegacyReceiptAsync(connection, deliveredEventId, commandId, occurredAtUtc,
                nextAttemptAtUtc, deliveredAtUtc: occurredAtUtc);
            await InsertLegacyReceiptAsync(connection, pendingEventId, commandId, occurredAtUtc,
                nextAttemptAtUtc, deliveredAtUtc: null);
        }

        await using SqliteEdgeAgentStore migratedStore = new(databasePath, 10);
        await migratedStore.InitializeAsync(CancellationToken.None);

        Assert.Equal(1, await migratedStore.GetPendingReceiptCountAsync(CancellationToken.None));
        PendingCommandReceipt migrated = Assert.IsType<PendingCommandReceipt>(
            await migratedStore.GetNextReceiptAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(pendingEventId, migrated.EventId);
        Assert.Equal(commandId, migrated.CommandId);
        Assert.Equal(7L, migrated.Sequence);
        Assert.Equal(DeviceCommandState.Failed, migrated.State);
        Assert.Equal("agent_restart", migrated.DiagnosticCode);
        Assert.Equal(2, migrated.AttemptCount);
        Assert.Equal(occurredAtUtc, migrated.OccurredAtUtc);
        Assert.Equal(nextAttemptAtUtc, migrated.NextAttemptAtUtc);
    }

    [Fact]
    public async Task Initialize_RejectsUnsupportedLegacyOutboxWithoutChangingItsColumns()
    {
        var databasePath = Path.Combine(_directory, "unsupported-outbox.db");
        await using (SqliteConnection connection = CreateConnection(databasePath))
        {
            await connection.OpenAsync();
            await using SqliteCommand create = connection.CreateCommand();
            create.CommandText = "CREATE TABLE receipt_outbox (event_id TEXT NOT NULL, command_id TEXT NOT NULL);";
            await create.ExecuteNonQueryAsync();
        }

        await using SqliteEdgeAgentStore incompatibleStore = new(databasePath, 10);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => incompatibleStore.InitializeAsync(CancellationToken.None));

        Assert.Contains("не підтримується", exception.Message, StringComparison.Ordinal);
        await using SqliteConnection verification = CreateConnection(databasePath);
        await verification.OpenAsync();
        await using SqliteCommand columnsQuery = verification.CreateCommand();
        columnsQuery.CommandText = "PRAGMA table_info(receipt_outbox);";
        await using SqliteDataReader reader = await columnsQuery.ExecuteReaderAsync();
        List<string> columnNames = [];
        while (await reader.ReadAsync())
        {
            columnNames.Add(reader.GetString(1));
        }

        Assert.Equal(new[] { "event_id", "command_id" }, columnNames);
    }

    [Fact]
    public async Task RecordRejectedCommand_DoesNotReplaceExistingCommand()
    {
        CommandDispatch command = CreateCommand(Guid.NewGuid(), 1);
        await _store.TryAcceptAsync(command, new string('a', 64), DateTimeOffset.UtcNow, CancellationToken.None);

        var recorded = await _store.RecordRejectedCommandAsync(
            command,
            new string('b', 64),
            DeviceCommandState.Rejected,
            "duplicate_command",
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.False(recorded);
        ProcessedCommand stored = Assert.IsType<ProcessedCommand>(
            await _store.FindCommandAsync(command.CommandId, CancellationToken.None));
        Assert.Equal(DeviceCommandState.Accepted, stored.State);
        Assert.Equal(new string('a', 64), stored.PayloadHash);
        Assert.Equal(1, await _store.GetPendingReceiptCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FindCommand_ReturnsNullForUnknownId()
    {
        Assert.Null(await _store.FindCommandAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task GetLastSequence_ReturnsZeroForLocationWithoutStoredSequence()
    {
        Assert.Equal(0L, await _store.GetLastSequenceAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndRejectsFurtherStoreCalls()
    {
        await _store.DisposeAsync();
        await _store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => _store.FindCommandAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private static CommandDispatch CreateCommand(Guid commandId, long sequence) => new(
        1, commandId, Guid.NewGuid(), LocationId, sequence, DeviceCommandAction.Start,
        "air_raid", new string('a', 64), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5), 900, null);

    private static CommandDispatch CreateStopCommand(Guid commandId, long sequence, Guid targetCommandId) => new(
        1, commandId, Guid.NewGuid(), LocationId, sequence, DeviceCommandAction.Stop,
        null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), 0, targetCommandId);

    private static SqliteConnection CreateConnection(string databasePath) => new(
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());

    private static async Task InsertLegacyReceiptAsync(
        SqliteConnection connection,
        Guid eventId,
        Guid commandId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset nextAttemptAtUtc,
        DateTimeOffset? deliveredAtUtc)
    {
        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO receipt_outbox
                (event_id, command_id, sequence, state, occurred_at_utc, diagnostic_code,
                 attempt_count, next_attempt_at_utc, delivered_at_utc)
            VALUES ($event, $command, 7, 'Failed', $occurred, 'agent_restart',
                    2, $next, $delivered);
            """;
        insert.Parameters.AddWithValue("$event", eventId.ToString("D"));
        insert.Parameters.AddWithValue("$command", commandId.ToString("D"));
        insert.Parameters.AddWithValue("$occurred", occurredAtUtc.ToUniversalTime().ToString("O"));
        insert.Parameters.AddWithValue("$next", nextAttemptAtUtc.ToUniversalTime().ToString("O"));
        object deliveredValue = deliveredAtUtc is DateTimeOffset value
            ? value.ToUniversalTime().ToString("O")
            : DBNull.Value;
        insert.Parameters.AddWithValue("$delivered", deliveredValue);
        await insert.ExecuteNonQueryAsync();
    }
}
