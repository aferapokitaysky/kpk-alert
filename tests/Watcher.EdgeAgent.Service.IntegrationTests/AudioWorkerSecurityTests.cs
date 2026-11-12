using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.AudioWorker;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Service.IntegrationTests;

public sealed class AudioWorkerSecurityTests
{
    [Fact]
    public void CommandRegistry_RemainsAvailableBeyondRetentionLimitAndRejectsOldSequenceReplay()
    {
        AudioWorkerCommandRegistry registry = new();
        string payloadHash = new('A', 64);
        Guid firstCommandId = Guid.Empty;
        string? error;

        for (var sequence = 1; sequence <= AudioWorkerCommandRegistry.MaximumRememberedCommands + 1; sequence++)
        {
            Guid commandId = Guid.NewGuid();
            if (sequence == 1) firstCommandId = commandId;
            Assert.True(registry.TryRegister(commandId, sequence, payloadHash,
                allowExactStopRetry: false, out error));
            Assert.Null(error);
        }

        Assert.Equal(AudioWorkerCommandRegistry.MaximumRememberedCommands, registry.RememberedCommandCount);
        Assert.True(registry.TryRegister(Guid.NewGuid(),
            AudioWorkerCommandRegistry.MaximumRememberedCommands + 2, payloadHash,
            allowExactStopRetry: false, out error));
        Assert.Null(error);

        Assert.False(registry.TryRegister(firstCommandId, 1, payloadHash,
            allowExactStopRetry: false, out error));
        Assert.Equal("stale_sequence", error);
    }

    [Fact]
    public void CommandRegistry_PreservesRecentDuplicateConflictAndStopRetryRules()
    {
        AudioWorkerCommandRegistry registry = new();
        Guid startId = Guid.NewGuid();
        Guid stopId = Guid.NewGuid();
        string payloadHash = new('A', 64);

        Assert.True(registry.TryRegister(startId, 1, payloadHash,
            allowExactStopRetry: false, out string? error));
        Assert.Null(error);
        Assert.False(registry.TryRegister(startId, 1, payloadHash,
            allowExactStopRetry: false, out error));
        Assert.Equal("duplicate_command", error);
        Assert.False(registry.TryRegister(startId, 1, new string('B', 64),
            allowExactStopRetry: true, out error));
        Assert.Equal("command_id_conflict", error);

        Assert.True(registry.TryRegister(stopId, 2, payloadHash,
            allowExactStopRetry: false, out error));
        Assert.Null(error);
        Assert.True(registry.TryRegister(stopId, 2, payloadHash,
            allowExactStopRetry: true, out error));
        Assert.Null(error);
    }

    [Fact]
    public async Task WorkerRestartDuringPlayback_ReportsFailureWithoutReplayingCommand()
    {
        var commandId = Guid.NewGuid();
        var startCount = 0;
        TaskCompletionSource<(Guid CommandId, DeviceCommandState State, string? DiagnosticCode)> finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeClock clock = new();
        EdgeAgentStatusProvider status = new(clock);
        AudioWorkerClient client = new(
            new EdgeAgentOptions
            {
                BaseAddress = new Uri("https://localhost"),
                IdentityPath = "/api/v1/device/identity",
                WebSocketPath = "/api/v1/device/commands/stream",
                ReceiptPath = "/api/v1/device/command-receipts",
                DatabasePath = "worker-test.db",
                AudioRootDirectory = Path.GetTempPath()
            },
            new TestPlaybackEventHandler(finished),
            status,
            NullLogger<AudioWorkerClient>.Instance,
            (request, _) =>
            {
                AudioWorkerResponse response = request.Operation switch
                {
                    "start" => StartResponse(request),
                    "get_playback_event" => Response(request, activeCommandId: null, activeAnchorId: null),
                    _ => Response(request)
                };
                return Task.FromResult(response);
            });

        AudioWorkerResponse StartResponse(AudioWorkerRequest request)
        {
            Interlocked.Increment(ref startCount);
            return Response(request, commandId, commandId);
        }

        await client.StartAsync(CancellationToken.None);
        try
        {
            await client.StartCommandAsync(
                new PlaybackRequest(commandId, "air_raid", Path.Combine(Path.GetTempPath(), "test.wav"),
                    new string('A', 64), DateTimeOffset.UtcNow.AddMinutes(5), "endpoint-1"),
                sequence: 1,
                payloadHash: new string('B', 64),
                CancellationToken.None);

            (Guid CommandId, DeviceCommandState State, string? DiagnosticCode) result =
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(TimeSpan.FromMilliseconds(400));

            Assert.Equal(commandId, result.CommandId);
            Assert.Equal(DeviceCommandState.Failed, result.State);
            Assert.Equal("audio_worker_unavailable", result.DiagnosticCode);
            Assert.Equal(1, Volatile.Read(ref startCount));
            Assert.Null(client.ActiveCommandId);
            Assert.Equal("unavailable", status.Get().AudioState);
        }
        finally
        {
            await client.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Supervisor_RestartsWorkerAfterExitWithBoundedBackoff()
    {
        using CancellationTokenSource cancellation = new();
        var starts = 0;
        List<TimeSpan> delays = [];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Watcher.EdgeAgent.AudioWorker.Program.RunSupervisionLoopAsync(
            _ =>
            {
                if (Interlocked.Increment(ref starts) == 1) throw new IOException("Simulated worker exit.");
                return Task.CompletedTask;
            },
            (delay, token) =>
            {
                delays.Add(delay);
                if (delays.Count == 2)
                {
                    cancellation.Cancel();
                    return Task.FromCanceled(token);
                }
                return Task.CompletedTask;
            },
            cancellation.Token));

        Assert.Equal(2, starts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], delays);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 4)]
    [InlineData(3, 8)]
    [InlineData(4, 16)]
    [InlineData(5, 30)]
    [InlineData(100, 30)]
    public void SupervisorRestartDelay_IsBounded(int failures, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), Watcher.EdgeAgent.AudioWorker.Program.GetRestartDelay(failures));
    }

    [Fact]
    public void PipeAcl_AllowsOnlyServiceReadWriteWithoutCreatingServerInstances()
    {
        SecurityIdentifier serviceSid = new("S-1-5-80-111-222-333-444-555");

        var accessSddl = AudioWorkerPipeServer.GetPipeAccessSddl(serviceSid);

        Assert.Contains(serviceSid.Value, accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;WD)", accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain(";;;AU)", accessSddl, StringComparison.Ordinal);
        Assert.DoesNotContain("0x4", accessSddl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(4, 4, true)]
    [InlineData(4, 5, false)]
    [InlineData(0, 0, false)]
    [InlineData(4, uint.MaxValue, false)]
    public void SessionValidation_UsesOnlyActiveConsoleSession(int workerSessionId, uint activeSessionId, bool expected)
    {
        Assert.Equal(expected, AudioWorkerPipeServer.IsAllowedSession(workerSessionId, activeSessionId));
    }

    private static AudioWorkerResponse Response(
        AudioWorkerRequest request,
        Guid? activeCommandId = null,
        Guid? activeAnchorId = null) =>
        new(1, request.RequestId, true, ActiveCommandId: activeCommandId,
            ActiveEpisodeAnchorCommandId: activeAnchorId);

    private sealed class TestPlaybackEventHandler(
        TaskCompletionSource<(Guid CommandId, DeviceCommandState State, string? DiagnosticCode)> finished)
        : IAudioPlaybackEventHandler
    {
        public Task OnPlaybackFinishedAsync(Guid commandId, DeviceCommandState state,
            string? diagnosticCode, CancellationToken cancellationToken)
        {
            finished.TrySetResult((commandId, state, diagnosticCode));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IEdgeAgentClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long GetTimestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
        public TimeSpan GetElapsedTime(long startingTimestamp) => System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp);
    }
}
