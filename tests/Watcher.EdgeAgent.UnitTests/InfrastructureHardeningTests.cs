using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;
using Watcher.EdgeAgent.Infrastructure.Persistence;
using Xunit.Sdk;

namespace Watcher.EdgeAgent.UnitTests;

public sealed class InfrastructureHardeningTests
{
    private static readonly Guid LocationId = Guid.Parse("f3d2797b-0b05-4db5-9cea-26ea46fd8263");

    [Fact]
    public async Task Initialize_MigratesLegacyOutboxAndPreservesReceipt()
    {
        using TemporaryDirectory directory = new("kgta-edge-legacy-migration");
        var databasePath = Path.Combine(directory.Path, "agent.db");
        var eventId = Guid.NewGuid();
        var commandId = Guid.NewGuid();

        await using (SqliteConnection connection = Open(databasePath))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, """
                CREATE TABLE receipt_outbox (
                    event_id TEXT PRIMARY KEY,
                    command_id TEXT NOT NULL,
                    sequence INTEGER NOT NULL,
                    state TEXT NOT NULL,
                    occurred_at_utc TEXT NOT NULL,
                    diagnostic_code TEXT NULL,
                    attempt_count INTEGER NOT NULL DEFAULT 0,
                    next_attempt_at_utc TEXT NOT NULL,
                    delivered_at_utc TEXT NULL
                );
                INSERT INTO receipt_outbox
                    (event_id, command_id, sequence, state, occurred_at_utc, diagnostic_code,
                     attempt_count, next_attempt_at_utc, delivered_at_utc)
                VALUES ($event, $command, 7, 'Accepted', '2026-11-05T09:00:00.0000000+00:00', NULL,
                        2, '2026-11-05T09:01:00.0000000+00:00', NULL);
                """,
                ("$event", eventId.ToString("D")), ("$command", commandId.ToString("D")));
        }

        await using SqliteEdgeAgentStore store = new(databasePath, 10);
        await store.InitializeAsync(CancellationToken.None);
        PendingCommandReceipt receipt = Assert.IsType<PendingCommandReceipt>(
            await store.GetNextReceiptAsync(DateTimeOffset.Parse("2026-11-05T09:02:00Z"), CancellationToken.None));

        Assert.Equal(eventId, receipt.EventId);
        Assert.Equal(commandId, receipt.CommandId);
        Assert.Equal(7L, receipt.Sequence);
        Assert.Equal(2, receipt.AttemptCount);
    }

    [Fact]
    public async Task FailedMigration_RollsBackAllDdlAndCanRetryAfterSchemaConflictIsRemoved()
    {
        using TemporaryDirectory directory = new("kgta-edge-migration-rollback");
        var databasePath = Path.Combine(directory.Path, "agent.db");
        await using (SqliteConnection connection = Open(databasePath))
        {
            await connection.OpenAsync();
            await ExecuteAsync(connection, "CREATE VIEW receipt_outbox AS SELECT 1 AS placeholder;");
        }

        await using SqliteEdgeAgentStore store = new(databasePath, 10);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(CancellationToken.None));

        await using (SqliteConnection connection = Open(databasePath))
        {
            await connection.OpenAsync();
            Assert.Equal(0L, await ScalarLongAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('agent_metadata', 'processed_commands');"));
            Assert.Equal(0L, await ScalarLongAsync(connection, "PRAGMA user_version;"));
            await ExecuteAsync(connection, "DROP VIEW receipt_outbox;");
        }

        await store.InitializeAsync(CancellationToken.None);
        Assert.Equal(0, await store.GetPendingReceiptCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CorruptDatabase_IsPreservedAndEachRecoveryCopyGetsANewName()
    {
        using TemporaryDirectory directory = new("kgta-edge-corrupt-db");
        var databasePath = Path.Combine(directory.Path, "agent.db");
        byte[] original = [0x00, 0x7F, 0x13, 0xFF, 0x01, 0x42, 0x99];
        await File.WriteAllBytesAsync(databasePath, original);
        await using SqliteEdgeAgentStore store = new(databasePath, 10);

        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InitializeAsync(CancellationToken.None));

        Assert.Equal(original, await File.ReadAllBytesAsync(databasePath));
        var copies = Directory.GetFiles(directory.Path, "agent.db.corrupt-*.bak");
        Assert.Equal(2, copies.Length);
        foreach (var copy in copies)
        {
            Assert.Equal(original, await File.ReadAllBytesAsync(copy));
        }
    }

    [Fact]
    public async Task Dispose_IsIdempotentAndRejectsLaterInitialization()
    {
        using TemporaryDirectory directory = new("kgta-edge-dispose");
        SqliteEdgeAgentStore store = new(Path.Combine(directory.Path, "agent.db"), 10);
        await store.InitializeAsync(CancellationToken.None);

        await store.DisposeAsync();
        await store.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.InitializeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.GetPendingReceiptCountAsync(CancellationToken.None));
    }

    [Fact]
    public async Task QueueLimitAndCommandIdempotency_AreEnforcedAtBoundaries()
    {
        using TemporaryDirectory directory = new("kgta-edge-store-boundaries");
        var databasePath = Path.Combine(directory.Path, "agent.db");
        await using SqliteEdgeAgentStore store = new(databasePath, 3);
        await store.InitializeAsync(CancellationToken.None);
        string payloadHash = new('A', 64);

        for (var sequence = 1; sequence <= 3; sequence++)
        {
            Assert.True(await store.RecordRejectedCommandAsync(CreateCommand(Guid.NewGuid(), sequence), payloadHash,
                DeviceCommandState.Rejected, "preset_missing", DateTimeOffset.UtcNow, CancellationToken.None));
        }
        Assert.False(await store.RecordRejectedCommandAsync(CreateCommand(Guid.NewGuid(), 4), payloadHash,
            DeviceCommandState.Rejected, "preset_missing", DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(3, await store.GetPendingReceiptCountAsync(CancellationToken.None));

        for (var receiptIndex = 0; receiptIndex < 3; receiptIndex++)
        {
            PendingCommandReceipt receipt = Assert.IsType<PendingCommandReceipt>(
                await store.GetNextReceiptAsync(DateTimeOffset.UtcNow.AddMinutes(1), CancellationToken.None));
            await store.MarkReceiptDeliveredAsync(receipt.EventId, CancellationToken.None);
        }
        CommandDispatch acceptedCommand = CreateCommand(Guid.NewGuid(), 1);
        Assert.Equal(AcceptCommandResult.Accepted,
            await store.TryAcceptAsync(acceptedCommand, payloadHash, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(AcceptCommandResult.Duplicate,
            await store.TryAcceptAsync(acceptedCommand, payloadHash, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(AcceptCommandResult.DuplicateIdConflict,
            await store.TryAcceptAsync(acceptedCommand, new string('B', 64), DateTimeOffset.UtcNow, CancellationToken.None));
    }

    [Fact]
    public async Task AudioResolver_AcceptsManifestLimitAnd900SecondPcm_Rejects901Seconds()
    {
        using TemporaryDirectory directory = new("kgta-edge-audio-boundaries");
        LocalAudioAssetResolver resolver = new(directory.Path);
        var locationDirectory = CreateLocationDirectory(directory.Path);
        var audioPath = Path.Combine(locationDirectory, "alarm.wav");
        await WritePcmWaveAsync(audioPath, durationSeconds: 900);
        var fileHash = await HashFileAsync(audioPath);
        var manifest = CreateManifest(LocationId, "air_raid", "alarm.wav", fileHash).PadRight(64 * 1024);
        var manifestPath = Path.Combine(locationDirectory, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false));
        var manifestHash = await HashFileAsync(manifestPath);

        LocalAudioAsset? asset = await resolver.ResolveAsync(LocationId, "air_raid", manifestHash, CancellationToken.None);
        Assert.NotNull(asset);
        Assert.Equal(fileHash, asset.Sha256);

        await WritePcmWaveAsync(audioPath, durationSeconds: 901);
        fileHash = await HashFileAsync(audioPath);
        manifest = CreateManifest(LocationId, "air_raid", "alarm.wav", fileHash);
        await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false));
        manifestHash = await HashFileAsync(manifestPath);
        Assert.Null(await resolver.ResolveAsync(LocationId, "air_raid", manifestHash, CancellationToken.None));
    }

    [Fact]
    public async Task AudioResolver_RejectsOversizedManifestFileAndHashMismatch()
    {
        using TemporaryDirectory directory = new("kgta-edge-audio-size");
        LocalAudioAssetResolver resolver = new(directory.Path);
        var locationDirectory = CreateLocationDirectory(directory.Path);
        var audioPath = Path.Combine(locationDirectory, "alarm.wav");
        await WritePcmWaveAsync(audioPath, 1);
        var fileHash = await HashFileAsync(audioPath);
        var baseManifest = CreateManifest(LocationId, "air_raid", "alarm.wav", fileHash);
        var manifestPath = Path.Combine(locationDirectory, "manifest.json");

        var oversized = baseManifest.PadRight(64 * 1024 + 1);
        await File.WriteAllTextAsync(manifestPath, oversized, new UTF8Encoding(false));
        Assert.Null(await resolver.ResolveAsync(LocationId, "air_raid", await HashFileAsync(manifestPath), CancellationToken.None));

        await File.WriteAllTextAsync(manifestPath, baseManifest, new UTF8Encoding(false));
        Assert.Null(await resolver.ResolveAsync(LocationId, "air_raid", new string('0', 64), CancellationToken.None));

        var traversalManifest = CreateManifest(LocationId, "air_raid", "../alarm.wav", fileHash);
        await File.WriteAllTextAsync(manifestPath, traversalManifest, new UTF8Encoding(false));
        Assert.Null(await resolver.ResolveAsync(LocationId, "air_raid", await HashFileAsync(manifestPath), CancellationToken.None));
    }

    [Fact]
    public async Task AudioResolver_RejectsAudioAboveMaximumFileSizeBeforeHashing()
    {
        using TemporaryDirectory directory = new("kgta-edge-audio-max-size");
        LocalAudioAssetResolver resolver = new(directory.Path);
        var locationDirectory = CreateLocationDirectory(directory.Path);
        var audioPath = Path.Combine(locationDirectory, "large.wav");
        await using (FileStream audio = new(audioPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            audio.SetLength(100L * 1024 * 1024 + 1);
        }

        var manifestPath = Path.Combine(locationDirectory, "manifest.json");
        var manifest = CreateManifest(LocationId, "air_raid", "large.wav", new string('a', 64));
        await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false));

        Assert.Null(await resolver.ResolveAsync(LocationId, "air_raid", await HashFileAsync(manifestPath), CancellationToken.None));
    }

    [Fact]
    public async Task AudioResolver_RejectsReparsePointAndUncRoot()
    {
        using TemporaryDirectory directory = new("kgta-edge-audio-reparse");
        LocalAudioAssetResolver uncResolver = new("\\\\server\\share\\audio");
        Assert.Null(await uncResolver.ResolveAsync(LocationId, "air_raid", new string('a', 64), CancellationToken.None));

        var locationDirectory = CreateLocationDirectory(directory.Path);
        var externalAudio = Path.Combine(Path.GetTempPath(), $"kgta-external-{Guid.NewGuid():N}.wav");
        await WritePcmWaveAsync(externalAudio, 1);
        var linkPath = Path.Combine(locationDirectory, "linked.wav");
        try
        {
            File.CreateSymbolicLink(linkPath, externalAudio);
        }
        catch (UnauthorizedAccessException)
        {
            File.Delete(externalAudio);
            return;
        }
        catch (IOException)
        {
            File.Delete(externalAudio);
            return;
        }
        catch (PlatformNotSupportedException)
        {
            File.Delete(externalAudio);
            return;
        }

        try
        {
            var manifestPath = Path.Combine(locationDirectory, "manifest.json");
            var manifest = CreateManifest(LocationId, "air_raid", "linked.wav", await HashFileAsync(externalAudio));
            await File.WriteAllTextAsync(manifestPath, manifest, new UTF8Encoding(false));
            LocalAudioAssetResolver resolver = new(directory.Path);
            Assert.Null(await resolver.ResolveAsync(LocationId, "air_raid", await HashFileAsync(manifestPath), CancellationToken.None));
        }
        finally
        {
            File.Delete(linkPath);
            File.Delete(externalAudio);
        }
    }

    private static SqliteConnection Open(string path) => new(new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString());

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        foreach ((var name, var value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static CommandDispatch CreateCommand(Guid commandId, long sequence) => new(
        1, commandId, Guid.NewGuid(), LocationId, sequence, DeviceCommandAction.Start,
        "air_raid", new string('a', 64), DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow.AddMinutes(5), 900, null);

    private static string CreateLocationDirectory(string root)
    {
        var locationDirectory = Path.Combine(root, LocationId.ToString("D"));
        Directory.CreateDirectory(locationDirectory);
        return locationDirectory;
    }

    private static string CreateManifest(Guid locationId, string presetCode, string fileName, string sha256) =>
        $$"""{"schemaVersion":1,"locationId":"{{locationId:D}}","presets":[{"presetCode":"{{presetCode}}","fileName":"{{fileName}}","sha256":"{{sha256}}"}]}""";

    private static async Task<string> HashFileAsync(string path)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private static async Task WritePcmWaveAsync(string path, int durationSeconds)
    {
        const int sampleRate = 8_000;
        var dataLength = checked(durationSeconds * sampleRate);
        var header = new byte[44];
        "RIFF"u8.CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4, 4), 36 + dataLength);
        "WAVE"u8.CopyTo(header.AsSpan(8, 4));
        "fmt "u8.CopyTo(header.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16, 4), 16);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(24, 4), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28, 4), sampleRate);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(32, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(34, 2), 8);
        "data"u8.CopyTo(header.AsSpan(36, 4));
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(40, 4), dataLength);

        await using FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.WriteAsync(header);
        var silence = new byte[64 * 1024];
        var remaining = dataLength;
        while (remaining > 0)
        {
            var write = Math.Min(remaining, silence.Length);
            await stream.WriteAsync(silence.AsMemory(0, write));
            remaining -= write;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
