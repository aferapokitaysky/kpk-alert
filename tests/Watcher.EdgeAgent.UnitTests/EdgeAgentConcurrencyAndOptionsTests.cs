using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Persistence;

namespace Watcher.EdgeAgent.UnitTests;

public sealed class EdgeAgentConcurrencyAndOptionsTests : IAsyncLifetime
{
    private static readonly Guid DeviceId = Guid.Parse("0844f185-07c3-421c-8e15-d71bd3e4d67a");
    private static readonly Guid LocationId = Guid.Parse("f3d2797b-0b05-4db5-9cea-26ea46fd8263");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T09:00:00Z");
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kgta-edge-concurrency-tests", Guid.NewGuid().ToString("N"));
    private SqliteEdgeAgentStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _store = new SqliteEdgeAgentStore(Path.Combine(_directory, "agent.db"), 20);
        await _store.InitializeAsync(CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task ConcurrentStarts_OnlyOneStartsAudio_AndOtherIsRejectedAsBusy()
    {
        var player = new FakeAudioPlayer();
        DeviceCommandProcessor processor = CreateProcessor(player);
        DeviceIdentity identity = new(DeviceId, LocationId);

        CommandProcessingOutcome[] outcomes = await Task.WhenAll(
            processor.ProcessAsync(CreateCommand(1), identity, CancellationToken.None),
            processor.ProcessAsync(CreateCommand(2), identity, CancellationToken.None));

        Assert.Equal(1, player.StartCount);
        Assert.Single(outcomes, outcome => outcome.State == DeviceCommandState.Playing);
        Assert.Single(outcomes, outcome => outcome.DiagnosticCode == "audio_output_busy");
    }

    [Fact]
    public void Options_RejectsNonHttpsBaseAddressAndBaseAddressQuery()
    {
        Assert.Equal("invalid_server_url", CreateOptions(baseAddress: new Uri("http://watcher.example.edu.ua")).Validate());
        Assert.Equal("invalid_server_url", CreateOptions(baseAddress: new Uri("https://watcher.example.edu.ua/?mode=unsafe")).Validate());
    }

    [Fact]
    public void Options_RejectsUnsafeAndUnexpectedRoutes()
    {
        Assert.Equal("invalid_server_path", CreateOptions(identityPath: "/api/v1/device/identity/../admin").Validate());
        Assert.Equal("invalid_server_path", CreateOptions(webSocketPath: "/api/v1/operator/commands/stream").Validate());
        Assert.Equal("invalid_server_path", CreateOptions(identityPath: "/api/v1/device/identity/extra").Validate());
        Assert.Equal("invalid_server_path", CreateOptions(receiptPath: "/api/v1/device/command-receipts?device=other").Validate());
    }

    [Fact]
    public void Options_RejectsOutOfRangeLimits()
    {
        Assert.Equal("invalid_configuration", CreateOptions(maximumReceiptQueue: 0).Validate());
        Assert.Equal("invalid_configuration", CreateOptions(maximumReceiptQueue: 2).Validate());
        Assert.Equal("invalid_configuration", CreateOptions(maximumWebSocketMessageBytes: 32 * 1024 + 1).Validate());
        Assert.Equal("invalid_configuration", CreateOptions(maximumClockSkewSeconds: 601).Validate());
        Assert.Equal("invalid_configuration", CreateOptions(reconnectMaximumDelaySeconds: 0).Validate());
    }

    [Fact]
    public async Task RejectedCommandRedelivery_ReusesSameReceiptEventId()
    {
        var player = new FakeAudioPlayer();
        var assets = new FakeAssetResolver { Asset = null };
        DeviceCommandProcessor processor = CreateProcessor(player, assets);
        CommandDispatch command = CreateCommand(1);
        DeviceIdentity identity = new(DeviceId, LocationId);

        CommandProcessingOutcome first = await processor.ProcessAsync(command, identity, CancellationToken.None);
        PendingCommandReceipt firstReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(Now.AddDays(1), CancellationToken.None));
        await _store.MarkReceiptDeliveredAsync(firstReceipt.EventId, CancellationToken.None);

        CommandProcessingOutcome retry = await processor.ProcessAsync(command, identity, CancellationToken.None);
        PendingCommandReceipt retriedReceipt = Assert.IsType<PendingCommandReceipt>(
            await _store.GetNextReceiptAsync(Now.AddDays(1), CancellationToken.None));

        Assert.Equal("preset_missing", first.DiagnosticCode);
        Assert.True(retry.Duplicate);
        Assert.Equal(firstReceipt.EventId, retriedReceipt.EventId);
        Assert.Equal(firstReceipt.State, retriedReceipt.State);
        Assert.Equal(firstReceipt.DiagnosticCode, retriedReceipt.DiagnosticCode);
        Assert.Equal(0, player.StartCount);
    }

    private DeviceCommandProcessor CreateProcessor(FakeAudioPlayer player, FakeAssetResolver? assets = null) =>
        new(_store, assets ?? new FakeAssetResolver(), player, new FakeClock(),
            TimeSpan.FromSeconds(120), 20);

    private static EdgeAgentOptions CreateOptions(
        Uri? baseAddress = null,
        string identityPath = "/api/v1/device/identity",
        string webSocketPath = "/api/v1/device/commands/stream",
        string receiptPath = "/api/v1/device/command-receipts",
        int maximumClockSkewSeconds = 120,
        int maximumWebSocketMessageBytes = 32 * 1024,
        int maximumReceiptQueue = 10_000,
        int reconnectMaximumDelaySeconds = 30) => new()
        {
            BaseAddress = baseAddress ?? new Uri("https://watcher.example.edu.ua"),
            IdentityPath = identityPath,
            WebSocketPath = webSocketPath,
            ReceiptPath = receiptPath,
            DatabasePath = "C:\\ProgramData\\KGTA\\Alert\\edge-agent.db",
            AudioRootDirectory = "C:\\ProgramData\\KGTA\\Alert\\Audio",
            ClientCertificateThumbprint = new string('a', 40),
            MaximumClockSkewSeconds = maximumClockSkewSeconds,
            MaximumWebSocketMessageBytes = maximumWebSocketMessageBytes,
            MaximumReceiptQueue = maximumReceiptQueue,
            ReconnectMaximumDelaySeconds = reconnectMaximumDelaySeconds
        };

    private static CommandDispatch CreateCommand(long sequence) => new(
        DeviceCommandValidator.CurrentSchemaVersion,
        Guid.NewGuid(),
        DeviceId,
        LocationId,
        sequence,
        DeviceCommandAction.Start,
        "air_raid",
        new string('a', 64),
        Now,
        Now.AddMinutes(5),
        900,
        null);

    private sealed class FakeAssetResolver : IAudioAssetResolver
    {
        public LocalAudioAsset? Asset { get; init; } = new("air_raid", new string('a', 64), "test.wav", "hash");

        public Task<LocalAudioAsset?> ResolveAsync(Guid locationId, string presetCode, string manifestHash,
            CancellationToken cancellationToken) => Task.FromResult(Asset);
    }

    private sealed class FakeAudioPlayer : IAudioPlayer
    {
        private int _startCount;
        public Guid? ActiveCommandId { get; private set; }
        public int StartCount => Volatile.Read(ref _startCount);

        public async Task StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            await Task.Yield();
            ActiveCommandId = request.CommandId;
        }

        public Task<bool> StopAsync(Guid targetCommandId, CancellationToken cancellationToken)
        {
            var stopped = ActiveCommandId == targetCommandId;
            if (stopped)
            {
                ActiveCommandId = null;
            }

            return Task.FromResult(stopped);
        }
    }

    private sealed class FakeClock : IEdgeAgentClock
    {
        public DateTimeOffset UtcNow => Now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
