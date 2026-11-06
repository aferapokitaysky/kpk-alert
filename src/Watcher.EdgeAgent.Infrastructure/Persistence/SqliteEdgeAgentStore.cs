using Microsoft.Data.Sqlite;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Persistence;

public sealed class SqliteEdgeAgentStore(string databasePath, int maximumReceiptQueue) : IEdgeAgentStore, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SqliteConnection? _connection;
    private volatile bool _disposed;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        SqliteConnection? connection = null;
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (maximumReceiptQueue < EdgeAgentOptions.StartCommandReceiptCapacity)
            {
                throw new InvalidOperationException("Черга квитанцій має вміщувати стани прийняття, запуску та завершення.");
            }
            if (_connection is not null)
            {
                return;
            }

            await ValidateExistingDatabaseAsync(databasePath, cancellationToken);

            var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);

            await using (SqliteCommand pragmas = connection.CreateCommand())
            {
                pragmas.CommandText = """
                    PRAGMA foreign_keys = ON;
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = FULL;
                    PRAGMA busy_timeout = 5000;
                    """;
                await pragmas.ExecuteNonQueryAsync(cancellationToken);
            }

            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand currentVersion = connection.CreateCommand();
            currentVersion.Transaction = transaction;
            currentVersion.CommandText = "PRAGMA user_version;";
            var schemaVersion = Convert.ToInt32(await currentVersion.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion > 1)
            {
                throw new InvalidOperationException("Версія локальної бази Edge Agent новіша за підтримувану.");
            }

            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS agent_metadata (
                    name TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS processed_commands (
                    command_id TEXT PRIMARY KEY,
                    sequence INTEGER NOT NULL,
                    payload_hash TEXT NOT NULL,
                    action TEXT NOT NULL,
                    state TEXT NOT NULL,
                    diagnostic_code TEXT NULL,
                    expires_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            await EnsureReceiptOutboxSchemaAsync(connection, transaction, cancellationToken);
            await using SqliteCommand finishMigration = connection.CreateCommand();
            finishMigration.Transaction = transaction;
            finishMigration.CommandText = """
                CREATE INDEX IF NOT EXISTS ix_receipt_outbox_due
                    ON receipt_outbox(delivered_at_utc, next_attempt_at_utc, receipt_id);
                PRAGMA user_version = 1;
                """;
            await finishMigration.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _connection = connection;
            connection = null;
        }
        finally
        {
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            _gate.Release();
        }
    }

    private static async Task ValidateExistingDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return;
        }

        Exception? corruption = null;
        string? result = null;
        try
        {
            await using SqliteConnection connection = new(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "PRAGMA quick_check;";
            result = Convert.ToString(await check.ExecuteScalarAsync(cancellationToken),
                System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
        {
            corruption = exception;
        }

        if (corruption is null && string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await PreserveCorruptDatabaseCopiesAsync(path, cancellationToken);
        throw new InvalidDataException(
            "Локальна база Edge Agent пошкоджена. Створено окрему копію для відновлення; база не була перезаписана.",
            corruption);
    }

    private static async Task PreserveCorruptDatabaseCopiesAsync(string path, CancellationToken cancellationToken)
    {
        var suffix = $".corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak";
        foreach (var source in new[] { path, path + "-wal", path + "-shm" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(source))
            {
                continue;
            }

            await using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using FileStream output = new(source + suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }

    private static async Task EnsureReceiptOutboxSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand table = connection.CreateCommand();
        table.Transaction = transaction;
        table.CommandText = "SELECT type FROM sqlite_master WHERE name = 'receipt_outbox';";
        var storedType = await table.ExecuteScalarAsync(cancellationToken);
        var type = storedType is string text ? text : null;

        if (type is null)
        {
            await using SqliteCommand create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE receipt_outbox (
                    receipt_id INTEGER PRIMARY KEY AUTOINCREMENT,
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
            await create.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        if (!string.Equals(type, "table", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Структура локальної бази Edge Agent некоректна: receipt_outbox не є таблицею.");
        }

        HashSet<string> columns = new(StringComparer.OrdinalIgnoreCase);
        await using (SqliteCommand info = connection.CreateCommand())
        {
            info.Transaction = transaction;
            info.CommandText = "PRAGMA table_info(receipt_outbox);";
            await using SqliteDataReader reader = await info.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        if (columns.Contains("receipt_id"))
        {
            return;
        }

        string[] requiredColumns =
        ["event_id", "command_id", "sequence", "state", "occurred_at_utc", "diagnostic_code",
            "attempt_count", "next_attempt_at_utc", "delivered_at_utc"];
        if (requiredColumns.Any(column => !columns.Contains(column)))
        {
            throw new InvalidDataException("Структура локальної бази Edge Agent не підтримується; дані не змінено.");
        }

        await using SqliteCommand migrate = connection.CreateCommand();
        migrate.Transaction = transaction;
        migrate.CommandText = """
            CREATE TABLE receipt_outbox_v1 (
                receipt_id INTEGER PRIMARY KEY AUTOINCREMENT,
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
            INSERT INTO receipt_outbox_v1
                (event_id, command_id, sequence, state, occurred_at_utc, diagnostic_code,
                 attempt_count, next_attempt_at_utc, delivered_at_utc)
            SELECT event_id, command_id, sequence, state, occurred_at_utc, diagnostic_code,
                   attempt_count, next_attempt_at_utc, delivered_at_utc
            FROM receipt_outbox ORDER BY rowid;
            DROP TABLE receipt_outbox;
            ALTER TABLE receipt_outbox_v1 RENAME TO receipt_outbox;
            """;
        await migrate.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingCommandReceipt>> RecoverInterruptedPlaybackAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteConnection connection = GetConnection();
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand select = connection.CreateCommand();
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText = """
                SELECT command_id, sequence FROM processed_commands
                WHERE state = 'Playing' OR (state = 'Accepted' AND action = 'Start')
                ORDER BY sequence;
                """;
            List<(Guid CommandId, long Sequence)> interrupted = [];
            await using (SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    interrupted.Add((Guid.Parse(reader.GetString(0)), reader.GetInt64(1)));
                }
            }

            List<PendingCommandReceipt> receipts = [];
            foreach ((Guid commandId, var sequence) in interrupted)
            {
                await UpdateStateAsync(connection, transaction, commandId, DeviceCommandState.Failed,
                    "agent_restart", nowUtc, cancellationToken);
                receipts.Add(await InsertReceiptAsync(connection, transaction, commandId, sequence,
                    DeviceCommandState.Failed, nowUtc, "agent_restart", cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            return receipts;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProcessedCommand?> FindCommandAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteCommand command = GetConnection().CreateCommand();
            command.CommandText = """
                SELECT sequence, payload_hash, state, diagnostic_code
                FROM processed_commands WHERE command_id = $id;
                """;
            command.Parameters.AddWithValue("$id", commandId.ToString("D"));
            await using (command)
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                return await reader.ReadAsync(cancellationToken)
                    ? new ProcessedCommand(commandId, reader.GetInt64(0), reader.GetString(1),
                        Enum.Parse<DeviceCommandState>(reader.GetString(2)),
                        reader.IsDBNull(3) ? null : reader.GetString(3))
                    : null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetLastSequenceAsync(Guid locationId, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            await using SqliteCommand command = GetConnection().CreateCommand();
            command.CommandText = "SELECT value FROM agent_metadata WHERE name = $name;";
            command.Parameters.AddWithValue("$name", $"location_sequence:{locationId:D}");
            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value is string text && long.TryParse(text, out var sequence) ? sequence : 0;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RequeueLatestReceiptAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteCommand command = GetConnection().CreateCommand();
            command.CommandText = """
                UPDATE receipt_outbox SET delivered_at_utc = NULL, next_attempt_at_utc = $now
                WHERE receipt_id = (
                    SELECT receipt_id FROM receipt_outbox WHERE command_id = $id
                    ORDER BY receipt_id DESC LIMIT 1
                );
                """;
            command.Parameters.AddWithValue("$id", commandId.ToString("D"));
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.MinValue));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> RecordRejectedCommandAsync(
        CommandDispatch command,
        string payloadHash,
        DeviceCommandState state,
        string diagnosticCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteConnection connection = GetConnection();
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT payload_hash FROM processed_commands WHERE command_id = $id;";
            existing.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            if (await existing.ExecuteScalarAsync(cancellationToken) is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            if (await GetPendingReceiptCountAsync(connection, transaction, cancellationToken) >= maximumReceiptQueue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO processed_commands
                    (command_id, sequence, payload_hash, action, state, diagnostic_code, expires_at_utc, updated_at_utc)
                VALUES ($id, $sequence, $payload, $action, $state, $diagnostic, $expires, $updated);
                """;
            insert.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            insert.Parameters.AddWithValue("$sequence", command.Sequence);
            insert.Parameters.AddWithValue("$payload", payloadHash);
            insert.Parameters.AddWithValue("$action", command.Action.ToString());
            insert.Parameters.AddWithValue("$state", state.ToString());
            insert.Parameters.AddWithValue("$diagnostic", diagnosticCode);
            insert.Parameters.AddWithValue("$expires", Format(command.ExpiresAtUtc));
            insert.Parameters.AddWithValue("$updated", Format(nowUtc));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await InsertReceiptAsync(connection, transaction, command.CommandId, command.Sequence,
                state, nowUtc, diagnosticCode, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<AcceptCommandResult> TryAcceptAsync(
        CommandDispatch command,
        string payloadHash,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteConnection connection = GetConnection();
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand existing = connection.CreateCommand();
            existing.Transaction = transaction;
            existing.CommandText = "SELECT payload_hash FROM processed_commands WHERE command_id = $id;";
            existing.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            var existingHash = await existing.ExecuteScalarAsync(cancellationToken);
            if (existingHash is string storedHash)
            {
                await transaction.RollbackAsync(cancellationToken);
                return string.Equals(storedHash, payloadHash, StringComparison.Ordinal)
                    ? AcceptCommandResult.Duplicate
                    : AcceptCommandResult.DuplicateIdConflict;
            }

            var lastSequence = await GetLastSequenceAsync(connection, transaction, command.LocationId, cancellationToken);
            if (command.Sequence <= lastSequence)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AcceptCommandResult.StaleSequence;
            }

            var pending = await GetPendingReceiptCountAsync(connection, transaction, cancellationToken);
            var receiptCapacityRequired = command.Action == DeviceCommandAction.Start ? 3 : 1;
            if (pending + receiptCapacityRequired > maximumReceiptQueue)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AcceptCommandResult.QueueFull;
            }

            await using SqliteCommand insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO processed_commands
                    (command_id, sequence, payload_hash, action, state, diagnostic_code, expires_at_utc, updated_at_utc)
                VALUES ($id, $sequence, $payload, $action, 'Accepted', NULL, $expires, $updated);
                INSERT INTO agent_metadata(name, value) VALUES ($sequenceName, $sequenceValue)
                ON CONFLICT(name) DO UPDATE SET value = excluded.value;
                """;
            insert.Parameters.AddWithValue("$id", command.CommandId.ToString("D"));
            insert.Parameters.AddWithValue("$sequence", command.Sequence);
            insert.Parameters.AddWithValue("$payload", payloadHash);
            insert.Parameters.AddWithValue("$action", command.Action.ToString());
            insert.Parameters.AddWithValue("$expires", Format(command.ExpiresAtUtc));
            insert.Parameters.AddWithValue("$updated", Format(nowUtc));
            insert.Parameters.AddWithValue("$sequenceName", $"location_sequence:{command.LocationId:D}");
            insert.Parameters.AddWithValue("$sequenceValue", command.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await insert.ExecuteNonQueryAsync(cancellationToken);
            // Stop не підтверджуємо до аудіоефекту: інакше сервер припинить повторну доставку після збою.
            if (command.Action == DeviceCommandAction.Start)
            {
                await InsertReceiptAsync(connection, transaction, command.CommandId, command.Sequence,
                    DeviceCommandState.Accepted, nowUtc, null, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return AcceptCommandResult.Accepted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingCommandReceipt> AddReceiptAsync(
        Guid commandId,
        long sequence,
        DeviceCommandState state,
        DateTimeOffset occurredAtUtc,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            return await InsertReceiptAsync(GetConnection(), null, commandId, sequence,
                state, occurredAtUtc, diagnosticCode, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateCommandStateAsync(
        Guid commandId,
        DeviceCommandState state,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            await UpdateStateAsync(GetConnection(), null, commandId, state,
                diagnosticCode, DateTimeOffset.UtcNow, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TransitionCommandAsync(
        Guid commandId,
        DeviceCommandState state,
        string? diagnosticCode,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteConnection connection = GetConnection();
            await using SqliteTransaction transaction = connection.BeginTransaction();
            await using SqliteCommand select = connection.CreateCommand();
            select.Transaction = transaction;
            select.CommandText = "SELECT sequence FROM processed_commands WHERE command_id = $id;";
            select.Parameters.AddWithValue("$id", commandId.ToString("D"));
            var sequenceValue = await select.ExecuteScalarAsync(cancellationToken);
            if (sequenceValue is null)
            {
                throw new InvalidOperationException("Команду для зміни стану не знайдено в локальній базі.");
            }

            var sequence = Convert.ToInt64(sequenceValue, System.Globalization.CultureInfo.InvariantCulture);
            await UpdateStateAsync(connection, transaction, commandId, state,
                diagnosticCode, occurredAtUtc, cancellationToken);
            await InsertReceiptAsync(connection, transaction, commandId, sequence,
                state, occurredAtUtc, diagnosticCode, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> GetPendingReceiptCountAsync(CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            return await GetPendingReceiptCountAsync(GetConnection(), null, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingCommandReceipt?> GetNextReceiptAsync(
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteCommand command = GetConnection().CreateCommand();
            command.CommandText = """
                SELECT event_id, command_id, sequence, state, occurred_at_utc, diagnostic_code,
                       attempt_count, next_attempt_at_utc
                FROM receipt_outbox
                WHERE delivered_at_utc IS NULL AND next_attempt_at_utc <= $now
                AND NOT EXISTS (
                    SELECT 1 FROM receipt_outbox earlier
                    WHERE earlier.command_id = receipt_outbox.command_id
                      AND earlier.receipt_id < receipt_outbox.receipt_id
                      AND earlier.delivered_at_utc IS NULL
                )
                ORDER BY receipt_id LIMIT 1;
                """;
            command.Parameters.AddWithValue("$now", Format(nowUtc));
            await using (command)
            await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                return await reader.ReadAsync(cancellationToken)
                    ? ReadReceipt(reader)
                    : null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task MarkReceiptDeliveredAsync(Guid eventId, CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteCommand command = GetConnection().CreateCommand();
            command.CommandText = "UPDATE receipt_outbox SET delivered_at_utc = $now WHERE event_id = $id;";
            command.Parameters.AddWithValue("$id", eventId.ToString("D"));
            command.Parameters.AddWithValue("$now", Format(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ScheduleReceiptRetryAsync(
        Guid eventId,
        DateTimeOffset nextAttemptAtUtc,
        CancellationToken cancellationToken)
    {
        await EnterAsync(cancellationToken);
        try
        {
            SqliteCommand command = GetConnection().CreateCommand();
            command.CommandText = """
                UPDATE receipt_outbox SET attempt_count = attempt_count + 1, next_attempt_at_utc = $next
                WHERE event_id = $id AND delivered_at_utc IS NULL;
                """;
            command.Parameters.AddWithValue("$id", eventId.ToString("D"));
            command.Parameters.AddWithValue("$next", Format(nextAttemptAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SqliteConnection? connection = _connection;
            _connection = null;
            if (connection is not null)
            {
                await connection.DisposeAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnterAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        if (_disposed)
        {
            _gate.Release();
            throw new ObjectDisposedException(nameof(SqliteEdgeAgentStore));
        }
    }

    private SqliteConnection GetConnection() =>
        _connection ?? throw new InvalidOperationException("Сховище Edge Agent ще не ініціалізоване.");

    private static async Task<long> GetLastSequenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM agent_metadata WHERE name = $name;";
        command.Parameters.AddWithValue("$name", $"location_sequence:{locationId:D}");
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text && long.TryParse(text, out var sequence) ? sequence : 0;
    }

    private static async Task<int> GetPendingReceiptCountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM receipt_outbox WHERE delivered_at_utc IS NULL;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<PendingCommandReceipt> InsertReceiptAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commandId,
        long sequence,
        DeviceCommandState state,
        DateTimeOffset occurredAtUtc,
        string? diagnosticCode,
        CancellationToken cancellationToken)
    {
        PendingCommandReceipt receipt = new(
            Guid.NewGuid(), commandId, sequence, state, occurredAtUtc,
            diagnosticCode, 0, occurredAtUtc);
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO receipt_outbox
                (event_id, command_id, sequence, state, occurred_at_utc, diagnostic_code, attempt_count, next_attempt_at_utc)
            VALUES ($event, $command, $sequence, $state, $occurred, $diagnostic, 0, $next);
            """;
        command.Parameters.AddWithValue("$event", receipt.EventId.ToString("D"));
        command.Parameters.AddWithValue("$command", commandId.ToString("D"));
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$occurred", Format(occurredAtUtc));
        command.Parameters.AddWithValue("$diagnostic", (object?)diagnosticCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$next", Format(occurredAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return receipt;
    }

    private static async Task UpdateStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid commandId,
        DeviceCommandState state,
        string? diagnosticCode,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE processed_commands SET state = $state, diagnostic_code = $diagnostic, updated_at_utc = $now
            WHERE command_id = $id;
            """;
        command.Parameters.AddWithValue("$id", commandId.ToString("D"));
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$diagnostic", (object?)diagnosticCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", Format(nowUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static PendingCommandReceipt ReadReceipt(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt64(2),
        Enum.Parse<DeviceCommandState>(reader.GetString(3)), DateTimeOffset.Parse(reader.GetString(4)),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6), DateTimeOffset.Parse(reader.GetString(7)));

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
}
