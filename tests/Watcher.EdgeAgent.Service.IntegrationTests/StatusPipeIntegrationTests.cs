using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Persistence;
using Watcher.EdgeAgent.Infrastructure.Transport;
using Watcher.EdgeAgent.Service;

namespace Watcher.EdgeAgent.Service.IntegrationTests;

public sealed class StatusPipeIntegrationTests
{
    [Fact]
    public void PipeCreationRejectsRemoteClientsAndKeepsByteMode()
    {
        const uint pipeTypeMessage = 0x00000004;
        const uint pipeReadModeMessage = 0x00000002;
        const uint pipeRejectRemoteClients = 0x00000008;
        var pipeMode = EdgeAgentStatusPipeService.CreatePipeMode();

        Assert.NotEqual(0u, pipeMode & pipeRejectRemoteClients);
        Assert.Equal(0u, pipeMode & pipeTypeMessage);
        Assert.Equal(0u, pipeMode & pipeReadModeMessage);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public async Task PipeReturnsStatusOnlyForVersionedReadOperation()
    {
        var pipeName = CreatePipeName();
        EdgeAgentStatusProvider status = new(new TestClock());
        status.Update(current => current with
        {
            ConnectionState = EdgeAgentConnectionState.Streaming,
            DeviceId = Guid.NewGuid(),
            PendingReceiptCount = 4
        });
        using EdgeAgentStatusPipeService service = CreateService(status, pipeName);
        await service.StartAsync(CancellationToken.None);

        try
        {
            EdgeAgentPipeResponse response = await SendRequestAsync(pipeName, new(1, "get_status"));
            Assert.True(response.Succeeded);
            Assert.NotNull(response.Status);
            Assert.NotNull(response.Status.LastStartedAtUtc);
            Assert.Equal(EdgeAgentConnectionState.Streaming, response.Status.ConnectionState);
            Assert.Equal(4, response.Status.PendingReceiptCount);

            EdgeAgentPipeResponse rejected = await SendRequestAsync(pipeName, new(1, "stop_service"));
            Assert.False(rejected.Succeeded);
            Assert.Null(rejected.Status);
            Assert.Equal("invalid_request", rejected.DiagnosticCode);

            EdgeAgentPipeResponse unsupportedVersion = await SendRequestAsync(pipeName, new(2, "get_status"));
            Assert.False(unsupportedVersion.Succeeded);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AudioMaintenanceRequestReturnsLeaseWithoutWaitingForClientDisconnect()
    {
        var pipeName = CreatePipeName();
        EdgeAgentMaintenanceCoordinator maintenance = new();
        EdgeAgentStatusProvider status = new(new TestClock());
        const string administratorSid = "S-1-5-21-111-222-333-1001";
        EdgeAgentStatusPipeService service = CreateService(status, pipeName, maintenance,
            privilegedCallerResolver: _ => (true, administratorSid));
        await service.StartAsync(CancellationToken.None);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            EdgeAgentPipeResponse begun = await SendRequestAsync(
                pipeName, new EdgeAgentPipeRequest(1, "begin_audio_maintenance"), timeout.Token);

            Assert.True(begun.Succeeded);
            Guid leaseId = Assert.IsType<Guid>(begun.LeaseId);
            Assert.Null(maintenance.TryBeginStart());

            EdgeAgentPipeResponse released = await SendRequestAsync(
                pipeName, new EdgeAgentPipeRequest(1, "release_maintenance", leaseId), timeout.Token);
            Assert.True(released.Succeeded);
            using EdgeAgentMaintenanceCoordinator.StartLease? resumed = maintenance.TryBeginStart();
            Assert.NotNull(resumed);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task LegacyMaintenanceClientWithoutAcknowledgementStillReceivesLease()
    {
        var pipeName = CreatePipeName();
        EdgeAgentMaintenanceCoordinator maintenance = new();
        EdgeAgentStatusProvider status = new(new TestClock());
        const string administratorSid = "S-1-5-21-111-222-333-1001";
        EdgeAgentStatusPipeService service = CreateService(status, pipeName, maintenance,
            privilegedCallerResolver: _ => (true, administratorSid));
        await service.StartAsync(CancellationToken.None);

        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(3));
            await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);
            var request = JsonSerializer.SerializeToUtf8Bytes(
                new EdgeAgentPipeRequest(1, "begin_maintenance"), JsonOptions);
            await WriteFrameAsync(pipe, request, timeout.Token);

            EdgeAgentPipeResponse response = JsonSerializer.Deserialize<EdgeAgentPipeResponse>(
                await ReadFrameAsync(pipe, timeout.Token), JsonOptions)!;

            Assert.True(response.Succeeded);
            Assert.True(maintenance.ReleaseMaintenance(Assert.IsType<Guid>(response.LeaseId)));
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisconnectWhileWaitingForAudioDrain_ReleasesPendingStartGate(bool sendAcknowledgement)
    {
        var pipeName = CreatePipeName();
        EdgeAgentMaintenanceCoordinator maintenance = new();
        EdgeAgentStatusProvider status = new(new TestClock());
        const string administratorSid = "S-1-5-21-111-222-333-1001";
        EdgeAgentStatusPipeService service = CreateService(status, pipeName, maintenance,
            privilegedCallerResolver: _ => (true, administratorSid));
        using EdgeAgentMaintenanceCoordinator.StartLease start = Assert.IsType<EdgeAgentMaintenanceCoordinator.StartLease>(
            maintenance.TryBeginStart());
        var commandId = Guid.NewGuid();
        start.MarkPlaybackStarted(commandId);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await using (NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                using CancellationTokenSource connectTimeout = new(TimeSpan.FromSeconds(5));
                await pipe.ConnectAsync(connectTimeout.Token);
                var request = JsonSerializer.SerializeToUtf8Bytes(
                    new EdgeAgentPipeRequest(1, sendAcknowledgement ? "begin_audio_maintenance" : "begin_maintenance"),
                    JsonOptions);
                await WriteFrameAsync(pipe, request, CancellationToken.None);
                if (sendAcknowledgement)
                {
                    await pipe.WriteAsync(new byte[] { 0xA5 });
                    await pipe.FlushAsync();
                }
                await WaitUntilAsync(() =>
                {
                    using EdgeAgentMaintenanceCoordinator.StartLease? attempted = maintenance.TryBeginStart();
                    return attempted is null;
                });
            }

            await WaitUntilAsync(() =>
            {
                using EdgeAgentMaintenanceCoordinator.StartLease? resumed = maintenance.TryBeginStart();
                return resumed is not null;
            });
            maintenance.PlaybackFinished(commandId);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task MaintenancePipeRequestBlocksStartsUntilPlaybackEndsAndLeaseOwnerReleases()
    {
        EdgeAgentMaintenanceCoordinator maintenance = new();
        EdgeAgentStatusProvider status = new(new TestClock());
        EdgeAgentStatusPipeService service = CreateService(status, CreatePipeName(), maintenance);
        var administratorSid = "S-1-5-21-111-222-333-1001";
        var otherAdministratorSid = "S-1-5-21-111-222-333-1002";
        var commandId = Guid.NewGuid();
        using EdgeAgentMaintenanceCoordinator.StartLease start = Assert.IsType<EdgeAgentMaintenanceCoordinator.StartLease>(
            maintenance.TryBeginStart());
        start.MarkPlaybackStarted(commandId);
        Task<EdgeAgentPipeResponse> beginTask = service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "begin_maintenance"), administratorSid, CancellationToken.None);

        Assert.False(beginTask.IsCompleted);
        Assert.Null(maintenance.TryBeginStart());
        maintenance.PlaybackFinished(commandId);
        EdgeAgentPipeResponse begun = await beginTask;

        Assert.True(begun.Succeeded);
        Guid leaseId = Assert.IsType<Guid>(begun.LeaseId);
        EdgeAgentPipeResponse wrongOwner = await service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "release_maintenance", leaseId), otherAdministratorSid, CancellationToken.None);
        Assert.Equal("maintenance_lease_owner_mismatch", wrongOwner.DiagnosticCode);
        Assert.Null(maintenance.TryBeginStart());

        EdgeAgentPipeResponse released = await service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "release_maintenance", leaseId), administratorSid, CancellationToken.None);
        Assert.True(released.Succeeded);
        using EdgeAgentMaintenanceCoordinator.StartLease? resumedStart = maintenance.TryBeginStart();
        Assert.NotNull(resumedStart);
    }

    [Fact]
    public async Task MaintenancePipeRequestRejectsCallersWithoutAuthenticatedSid()
    {
        EdgeAgentMaintenanceCoordinator maintenance = new();
        EdgeAgentStatusProvider status = new(new TestClock());
        EdgeAgentStatusPipeService service = CreateService(status, CreatePipeName(), maintenance);

        EdgeAgentPipeResponse response = await service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "begin_maintenance"), null, CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal("maintenance_access_denied", response.DiagnosticCode);
        using EdgeAgentMaintenanceCoordinator.StartLease? start = maintenance.TryBeginStart();
        Assert.NotNull(start);
    }

    [Fact]
    public async Task AudioOutputSelection_RequiresLeaseOwnerAndPersistsOnlyAnActiveEndpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-audio-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "audio-endpoint.json");
            var control = new FakeAudioEndpointControl();
            EdgeAgentStatusProvider status = new(new TestClock());
            EdgeAgentAudioOutputManager manager = new("endpoint-old", settingsPath, control, status);
            EdgeAgentMaintenanceCoordinator maintenance = new();
            EdgeAgentStatusPipeService service = CreateService(status, CreatePipeName(), maintenance,
                audioOutputManager: manager);
            const string ownerSid = "S-1-5-21-111-222-333-1001";
            const string otherSid = "S-1-5-21-111-222-333-1002";

            EdgeAgentPipeResponse deniedWithoutLease = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "set_audio_output", Guid.NewGuid(), "endpoint-new"),
                ownerSid, CancellationToken.None);
            Assert.Equal("maintenance_lease_invalid", deniedWithoutLease.DiagnosticCode);

            EdgeAgentPipeResponse begun = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "begin_maintenance"), ownerSid, CancellationToken.None);
            Guid leaseId = Assert.IsType<Guid>(begun.LeaseId);

            EdgeAgentPipeResponse wrongOwner = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "set_audio_output", leaseId, "endpoint-new"),
                otherSid, CancellationToken.None);
            Assert.Equal("maintenance_lease_invalid", wrongOwner.DiagnosticCode);

            EdgeAgentPipeResponse invalidEndpoint = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "set_audio_output", leaseId, "\\\\server\\arbitrary"),
                ownerSid, CancellationToken.None);
            Assert.Equal("audio_output_unavailable", invalidEndpoint.DiagnosticCode);
            Assert.Equal("endpoint-old", manager.CurrentEndpointId);
            Assert.False(File.Exists(settingsPath));

            EdgeAgentPipeResponse changed = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "set_audio_output", leaseId, "endpoint-new"),
                ownerSid, CancellationToken.None);
            Assert.True(changed.Succeeded);
            Assert.Equal("endpoint-new", status.Get().AudioEndpointId);
            Assert.Equal("endpoint-new", manager.CurrentEndpointId);

            EdgeAgentAudioOutputManager restored = new("endpoint-old", settingsPath, control, status);
            Assert.Equal("endpoint-new", restored.CurrentEndpointId);

            EdgeAgentPipeResponse released = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "release_maintenance", leaseId), ownerSid, CancellationToken.None);
            Assert.True(released.Succeeded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InvalidSavedAudioOutput_FailsClosedInsteadOfUsingConfiguredFallback()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-invalid-audio-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var settingsPath = Path.Combine(directory, "audio-endpoint.json");
            await File.WriteAllTextAsync(settingsPath, "{ invalid json");
            EdgeAgentStatusProvider status = new(new TestClock());
            EdgeAgentAudioOutputManager manager = new("endpoint-old", settingsPath,
                new FakeAudioEndpointControl(), status);

            Assert.Null(manager.CurrentEndpointId);
            Assert.True(manager.HasInvalidSavedSettings);

            var databasePath = Path.Combine(directory, "edge-agent.db");
            await using SqliteEdgeAgentStore store = new(databasePath, 100);
            EdgeAgentOptions options = new()
            {
                BaseAddress = new Uri("https://watcher.example.invalid"),
                IdentityPath = "/api/v1/device/identity",
                WebSocketPath = "/api/v1/device/commands/stream",
                ReceiptPath = "/api/v1/device/command-receipts",
                DatabasePath = databasePath,
                AudioRootDirectory = directory,
                AudioEndpointId = "endpoint-old"
            };
            EdgeAgentStartupCoordinator startup = new(store, new TestClock(), options, status, manager);
            await startup.InitializeAsync(CancellationToken.None);

            Assert.Null(status.Get().AudioEndpointId);
            Assert.Equal("audio_output_configuration_invalid", status.Get().DiagnosticCode);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AudioOutputTest_RequiresLeaseAndUsesOnlyEnumeratedCandidate()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-audio-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var control = new FakeAudioEndpointControl();
            EdgeAgentStatusProvider status = new(new TestClock());
            EdgeAgentAudioOutputManager manager = new("endpoint-old", Path.Combine(directory, "selection.json"), control, status);
            EdgeAgentMaintenanceCoordinator maintenance = new();
            EdgeAgentStatusPipeService service = CreateService(status, CreatePipeName(), maintenance,
                audioOutputManager: manager);
            const string ownerSid = "S-1-5-21-111-222-333-1001";

            EdgeAgentPipeResponse deniedWithoutLease = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "test_audio_output", Guid.NewGuid(), "endpoint-new", AudioTestSound.Chime),
                ownerSid, CancellationToken.None);
            Assert.Equal("maintenance_lease_invalid", deniedWithoutLease.DiagnosticCode);

            EdgeAgentPipeResponse begun = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "begin_maintenance"), ownerSid, CancellationToken.None);
            Guid leaseId = Assert.IsType<Guid>(begun.LeaseId);

            EdgeAgentPipeResponse missing = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "test_audio_output", leaseId, "\\\\server\\arbitrary", AudioTestSound.Tone),
                ownerSid, CancellationToken.None);
            Assert.Equal("audio_test_failed", missing.DiagnosticCode);
            Assert.Null(control.LastTestEndpointId);

            EdgeAgentPipeResponse tested = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "test_audio_output", leaseId, "endpoint-new", AudioTestSound.Chime),
                ownerSid, CancellationToken.None);
            Assert.True(tested.Succeeded);
            Assert.Equal("endpoint-new", control.LastTestEndpointId);
            Assert.Equal(AudioTestSound.Chime, control.LastTestSound);
            Assert.Equal("endpoint-old", manager.CurrentEndpointId);
            Assert.False(File.Exists(Path.Combine(directory, "selection.json")));

            Assert.True((await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "release_maintenance", leaseId), ownerSid,
                CancellationToken.None)).Succeeded);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AudioOutputInventoryIsVisibleThroughTheLocalPipeWithoutMaintenanceLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-audio-list-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            FakeAudioEndpointControl control = new();
            EdgeAgentStatusProvider status = new(new TestClock());
            EdgeAgentAudioOutputManager manager = new("endpoint-old", Path.Combine(directory, "selection.json"), control, status);
            EdgeAgentStatusPipeService service = CreateService(status, CreatePipeName(), audioOutputManager: manager);

            EdgeAgentPipeResponse response = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "get_audio_outputs"), null, CancellationToken.None);

            Assert.True(response.Succeeded);
            Assert.Collection(Assert.IsAssignableFrom<IReadOnlyList<EdgeAgentAudioEndpoint>>(response.AudioEndpoints),
                endpoint => Assert.Equal("endpoint-old", endpoint.Id),
                endpoint => Assert.Equal("endpoint-new", endpoint.Id));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AudioOutputInventoryWithMaximumEscapedNamesFitsTheBoundedResponseFrame()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-audio-frame-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var pipeName = CreatePipeName();
        EdgeAgentAudioEndpoint[] outputs = Enumerable.Range(0, 16)
            .Select(index => new EdgeAgentAudioEndpoint(
                $"{index}{new string('\\', 512 - index.ToString().Length)}", new string('Ж', 256), index == 0))
            .ToArray();
        try
        {
            FakeAudioEndpointControl control = new(outputs);
            EdgeAgentStatusProvider status = new(new TestClock());
            EdgeAgentAudioOutputManager manager = new("endpoint-old", Path.Combine(directory, "selection.json"), control, status);
            EdgeAgentStatusPipeService service = CreateService(status, pipeName, audioOutputManager: manager);
            await service.StartAsync(CancellationToken.None);

            try
            {
                EdgeAgentPipeResponse response = await SendRequestAsync(pipeName,
                    new EdgeAgentPipeRequest(1, "get_audio_outputs"));

                Assert.True(response.Succeeded);
                Assert.Equal(16, response.AudioEndpoints?.Count);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AudioOutputMutationAndTestAreRejectedForAnUnauthenticatedPipeCaller()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-audio-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var pipeName = CreatePipeName();
        try
        {
            FakeAudioEndpointControl control = new();
            EdgeAgentStatusProvider status = new(new TestClock());
            EdgeAgentAudioOutputManager manager = new("endpoint-old", Path.Combine(directory, "selection.json"),
                control, status);
            SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User ??
                throw new InvalidOperationException("Тестовий процес не має Windows SID.");
            EdgeAgentStatusPipeService service = new(status, NullLogger<EdgeAgentStatusPipeService>.Instance,
                pipeName, () => currentUser, () => currentUser, audioOutputManager: manager,
                privilegedCallerResolver: _ => (false, null));
            await service.StartAsync(CancellationToken.None);

            try
            {
                EdgeAgentPipeResponse inventory = await SendRequestAsync(pipeName,
                    new EdgeAgentPipeRequest(1, "get_audio_outputs"));
                Assert.True(inventory.Succeeded);

                EdgeAgentPipeResponse setResponse = await SendRequestAsync(pipeName,
                    new EdgeAgentPipeRequest(1, "set_audio_output", Guid.NewGuid(), "endpoint-new"));
                Assert.Equal("maintenance_access_denied", setResponse.DiagnosticCode);

                EdgeAgentPipeResponse testResponse = await SendRequestAsync(pipeName,
                    new EdgeAgentPipeRequest(1, "test_audio_output", Guid.NewGuid(), "endpoint-new", AudioTestSound.Tone));
                Assert.Equal("maintenance_access_denied", testResponse.DiagnosticCode);
                Assert.Null(control.LastTestEndpointId);
                Assert.Equal("endpoint-old", manager.CurrentEndpointId);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DatabaseBackup_UsesConfiguredDatabaseAndKeepsMaintenanceLeaseOnLostResponse()
    {
        var root = Path.Combine(Path.GetTempPath(), $"kgta-edge-backup-{Guid.NewGuid():N}");
        var databasePath = Path.Combine(root, "data", "edge-agent.db");
        var stagingPath = Path.Combine(root, "backup-staging");
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        Directory.CreateDirectory(stagingPath);
        try
        {
            await using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString()))
            {
                await source.OpenAsync();
                await using SqliteCommand create = source.CreateCommand();
                create.CommandText = "CREATE TABLE backup_probe (value TEXT NOT NULL); INSERT INTO backup_probe VALUES ('from-configured-db');";
                await create.ExecuteNonQueryAsync();
            }

            EdgeAgentOptions options = new()
            {
                BaseAddress = new Uri("https://localhost"),
                IdentityPath = "/api/v1/device/identity",
                WebSocketPath = "/api/v1/device/commands/stream",
                ReceiptPath = "/api/v1/device/command-receipts",
                DatabasePath = databasePath,
                AudioRootDirectory = root
            };
            EdgeAgentDatabaseBackup backup = new(options, stagingPath);
            EdgeAgentMaintenanceCoordinator maintenance = new(TimeSpan.FromMinutes(1));
            EdgeAgentStatusPipeService service = CreateService(new EdgeAgentStatusProvider(new TestClock()),
                CreatePipeName(), maintenance, backup);
            const string administratorSid = "S-1-5-21-111-222-333-1001";
            EdgeAgentPipeResponse begun = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "begin_maintenance"), administratorSid, CancellationToken.None);
            Guid leaseId = Assert.IsType<Guid>(begun.LeaseId);

            EdgeAgentPipeResponse wrongOwner = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "backup_database", leaseId),
                "S-1-5-21-111-222-333-1002", CancellationToken.None);
            Assert.Equal("maintenance_lease_owner_mismatch", wrongOwner.DiagnosticCode);
            Assert.Empty(Directory.EnumerateFiles(stagingPath));

            EdgeAgentPipeResponse response = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "backup_database", leaseId), administratorSid, CancellationToken.None);
            Assert.True(response.Succeeded);
            Assert.Null(response.LeaseId);
            Assert.Equal(leaseId, response.MaintenanceLeaseId);
            Guid backupId = Assert.IsType<Guid>(response.BackupId);
            Assert.Equal(EdgeAgentDatabaseBackup.CreateDatabasePathHash(databasePath), response.DatabasePathHash);
            var backupPath = Path.Combine(stagingPath, $"edge-agent-{backupId:N}.db");
            Assert.True(File.Exists(backupPath));

            await using var restored = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = backupPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
            await restored.OpenAsync();
            await using SqliteCommand read = restored.CreateCommand();
            read.CommandText = "SELECT value FROM backup_probe;";
            Assert.Equal("from-configured-db", await read.ExecuteScalarAsync());

            await Assert.ThrowsAsync<IOException>(() => service.WriteResponseAsync(
                new DisconnectedWriteStream(), response, CancellationToken.None));
            Assert.True(maintenance.IsMaintenanceLeaseActive(leaseId));
            Assert.Null(maintenance.TryBeginStart());

            EdgeAgentPipeResponse released = await service.HandleRequestAsync(
                new EdgeAgentPipeRequest(1, "release_maintenance", leaseId), administratorSid, CancellationToken.None);
            Assert.True(released.Succeeded);
            using EdgeAgentMaintenanceCoordinator.StartLease? resumedStart = maintenance.TryBeginStart();
            Assert.NotNull(resumedStart);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LostMaintenanceResponse_ReleasesGateInsteadOfLeavingServiceQuiesced()
    {
        EdgeAgentMaintenanceCoordinator maintenance = new();
        EdgeAgentStatusPipeService service = CreateService(new EdgeAgentStatusProvider(new TestClock()),
            CreatePipeName(), maintenance);
        EdgeAgentPipeResponse response = await service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "begin_maintenance"), "S-1-5-21-111-222-333-1001", CancellationToken.None);
        Assert.True(response.Succeeded);
        Assert.Null(maintenance.TryBeginStart());

        await Assert.ThrowsAsync<IOException>(() => service.WriteResponseAsync(
            new DisconnectedWriteStream(), response, CancellationToken.None));

        using EdgeAgentMaintenanceCoordinator.StartLease? start = maintenance.TryBeginStart();
        Assert.NotNull(start);
    }

    [Fact]
    public async Task ExpiredMaintenanceLease_CanBeReplacedWithoutRestartingService()
    {
        EdgeAgentMaintenanceCoordinator maintenance = new(TimeSpan.FromMilliseconds(25));
        EdgeAgentStatusPipeService service = CreateService(new EdgeAgentStatusProvider(new TestClock()),
            CreatePipeName(), maintenance);
        const string administratorSid = "S-1-5-21-111-222-333-1001";

        EdgeAgentPipeResponse first = await service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "begin_maintenance"), administratorSid, CancellationToken.None);
        await Task.Delay(75);
        EdgeAgentPipeResponse next = await service.HandleRequestAsync(
            new EdgeAgentPipeRequest(1, "begin_maintenance"), administratorSid, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.True(next.Succeeded);
        Assert.NotEqual(first.LeaseId, next.LeaseId);
        Assert.True(maintenance.ReleaseMaintenance(next.LeaseId!.Value));
    }

    [Fact]
    public async Task PipeRejectsOversizedAndMalformedFramesAndContinuesServing()
    {
        var pipeName = CreatePipeName();
        EdgeAgentStatusProvider status = new(new TestClock());
        using EdgeAgentStatusPipeService service = CreateService(status, pipeName);
        await service.StartAsync(CancellationToken.None);

        try
        {
            await AssertRejectedFrameAsync(pipeName, [0x01, 0x40, 0x00, 0x00]);
            await AssertRejectedFrameAsync(pipeName, [0x01, 0x00, 0x00, 0x00, (byte)'{']);
            var unsupportedField = "{\"schemaVersion\":1,\"operation\":\"get_status\",\"command\":\"stop\"}"u8.ToArray();
            byte[] unsupportedFieldFrame = [.. BitConverter.GetBytes(unsupportedField.Length), .. unsupportedField];
            await AssertRejectedFrameAsync(pipeName, unsupportedFieldFrame);
            await AssertSlowClientReleasedAsync(pipeName);

            EdgeAgentPipeResponse response = await SendRequestAsync(pipeName, new(1, "get_status"));
            Assert.True(response.Succeeded);
            Assert.NotNull(response.Status);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void PipeAclGrantsOnlyServiceAdministratorsAndOperatorGroup()
    {
        SecurityIdentifier serviceSid = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier operatorSid = new(WellKnownSidType.BuiltinPowerUsersSid, null);
        PipeSecurity security = EdgeAgentStatusPipeService.CreatePipeSecurity(() => serviceSid, () => operatorSid);
        PipeAccessRule[] accessRules = security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();
        SecurityIdentifier[] grantedSids = accessRules
            .Select(rule => (SecurityIdentifier)rule.IdentityReference)
            .ToArray();

        Assert.Contains(serviceSid, grantedSids);
        Assert.Contains(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), grantedSids);
        Assert.Contains(operatorSid, grantedSids);
        Assert.DoesNotContain(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), grantedSids);
        Assert.Contains(accessRules, rule => rule.IdentityReference.Equals(serviceSid) &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
        Assert.DoesNotContain(accessRules, rule => rule.IdentityReference.Equals(operatorSid) &&
            rule.PipeAccessRights.HasFlag(PipeAccessRights.CreateNewInstance));
    }

    private static EdgeAgentStatusPipeService CreateService(
        EdgeAgentStatusProvider status,
        string pipeName,
        EdgeAgentMaintenanceCoordinator? maintenance = null,
        EdgeAgentDatabaseBackup? databaseBackup = null,
        EdgeAgentAudioOutputManager? audioOutputManager = null,
        Func<NamedPipeServerStream, (bool Authorized, string? CallerSid)>? privilegedCallerResolver = null)
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User ??
            throw new InvalidOperationException("Тестовий процес не має Windows SID.");
        return new EdgeAgentStatusPipeService(status, NullLogger<EdgeAgentStatusPipeService>.Instance,
            pipeName, () => currentUser, () => currentUser, maintenance, databaseBackup, audioOutputManager,
            privilegedCallerResolver);
    }

    private sealed class FakeAudioEndpointControl : IAudioEndpointControl
    {
        private readonly IReadOnlyList<EdgeAgentAudioEndpoint> _outputs;

        public FakeAudioEndpointControl(IReadOnlyList<EdgeAgentAudioEndpoint>? outputs = null) =>
            _outputs = outputs ??
            [
                new EdgeAgentAudioEndpoint("endpoint-old", "Старий вихід", true),
                new EdgeAgentAudioEndpoint("endpoint-new", "Новий вихід", false)
            ];

        public string? LastTestEndpointId { get; private set; }
        public AudioTestSound? LastTestSound { get; private set; }

        public IReadOnlyList<EdgeAgentAudioEndpoint> GetActiveOutputs() => _outputs;

        public Task PlayTestSoundAsync(string endpointId, AudioTestSound sound, CancellationToken cancellationToken)
        {
            if (!GetActiveOutputs().Any(endpoint => endpoint.Id == endpointId))
            {
                throw new InvalidOperationException("Вибраний аудіовихід недоступний.");
            }

            LastTestEndpointId = endpointId;
            LastTestSound = sound;
            return Task.CompletedTask;
        }
    }

    private sealed class DisconnectedWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new IOException("Клієнт закрив канал.");
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Клієнт закрив канал.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Клієнт закрив канал."));
    }

    private static async Task<EdgeAgentPipeResponse> SendRequestAsync(
        string pipeName,
        EdgeAgentPipeRequest request,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions), timeout.Token);
        if (request.Operation is "begin_maintenance" or "begin_audio_maintenance")
        {
            await pipe.WriteAsync(new byte[] { 0xA5 }, timeout.Token);
            await pipe.FlushAsync(timeout.Token);
        }

        var response = await ReadFrameAsync(pipe, timeout.Token);
        return JsonSerializer.Deserialize<EdgeAgentPipeResponse>(response, JsonOptions)
            ?? throw new InvalidDataException("Служба повернула порожню відповідь.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task AssertRejectedFrameAsync(string pipeName, byte[] frame)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(frame, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        var response = new byte[1];
        var read = await pipe.ReadAsync(response, timeout.Token);
        Assert.Equal(0, read);
    }

    private static async Task AssertSlowClientReleasedAsync(string pipeName)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(8));
        await using NamedPipeClientStream pipe = new(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        var response = new byte[1];
        var read = await pipe.ReadAsync(response, timeout.Token);
        Assert.Equal(0, read);

        EdgeAgentPipeResponse nextResponse = await SendRequestAsync(pipeName, new(1, "get_status"));
        Assert.True(nextResponse.Succeeded);
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken token)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(payload, token);
        await stream.FlushAsync(token);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, token);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        Assert.InRange(length, 1, 128 * 1024);
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], token);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static string CreatePipeName() => $"KGTA.Alert.EdgeAgent.Tests.{Guid.NewGuid():N}";

    private sealed class TestClock : IEdgeAgentClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long GetTimestamp() => Environment.TickCount64;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.FromMilliseconds(Environment.TickCount64 - startingTimestamp);
    }
}

public sealed class EdgeAgentStartupIntegrationTests
{
    [Fact]
    public void FullReceiptQueueDegradesStatusAndRecoversWhenBacklogFallsBelowLimit()
    {
        EdgeAgentStatusProvider status = new(new StartupTestClock(DateTimeOffset.UtcNow));
        status.UpdateTransportState(EdgeAgentConnectionState.Streaming);
        status.ReportDiagnostic("receipt_queue_full");

        Assert.Equal(EdgeAgentConnectionState.Degraded, status.Get().ConnectionState);
        Assert.Equal("receipt_queue_full", status.Get().DiagnosticCode);

        status.UpdatePendingReceiptCount(8, 10);
        Assert.Equal(EdgeAgentConnectionState.Degraded, status.Get().ConnectionState);

        status.UpdatePendingReceiptCount(7, 10);
        Assert.Equal(EdgeAgentConnectionState.Streaming, status.Get().ConnectionState);
        Assert.Null(status.Get().DiagnosticCode);
        Assert.Equal(7, status.Get().PendingReceiptCount);
    }

    [Fact]
    public void ReceiptQueuePressureReservesThreeSlotsForStartCommand()
    {
        EdgeAgentStatusProvider status = new(new StartupTestClock(DateTimeOffset.UtcNow));
        status.UpdateTransportState(EdgeAgentConnectionState.Streaming);

        status.UpdatePendingReceiptCount(98, 100);
        Assert.Equal(EdgeAgentConnectionState.Degraded, status.Get().ConnectionState);
        Assert.Equal("receipt_queue_full", status.Get().DiagnosticCode);

        status.UpdatePendingReceiptCount(97, 100);
        Assert.Equal(EdgeAgentConnectionState.Streaming, status.Get().ConnectionState);
        Assert.Null(status.Get().DiagnosticCode);
    }

    [Fact]
    public void LastStartedAtRemainsStableWhileStatusRefreshes()
    {
        var startedAt = DateTimeOffset.Parse("2026-11-06T10:00:00+00:00");
        StartupTestClock clock = new(startedAt);
        EdgeAgentStatusProvider status = new(clock);

        clock.UtcNow = startedAt.AddMinutes(5);
        status.UpdateTransportState(EdgeAgentConnectionState.Reconnecting);

        Assert.Equal(startedAt, status.Get().LastStartedAtUtc);
        Assert.Equal(clock.UtcNow, status.Get().ObservedAtUtc);
    }

    [Fact]
    public void AudioStateRequiresAnActiveConfiguredEndpointAndRecoversWhenItReturns()
    {
        Assert.Equal("unavailable", EdgeAgentHostedService.ResolveAudioState(null, null, false, "unavailable"));
        Assert.Equal("unavailable", EdgeAgentHostedService.ResolveAudioState("endpoint-1", null, false, "ready"));
        Assert.Equal("idle", EdgeAgentHostedService.ResolveAudioState("endpoint-1", null, true, "idle"));
        Assert.Equal("ready", EdgeAgentHostedService.ResolveAudioState("endpoint-1", null, true, "unavailable"));
        Assert.Equal("playing", EdgeAgentHostedService.ResolveAudioState("endpoint-1",
            new ProcessedCommand(Guid.NewGuid(), 1, "hash", DeviceCommandState.Playing, null), false, "ready"));
    }

    [Theory]
    [InlineData(DeviceCommandState.Failed, "audio_output_unavailable", "unavailable")]
    [InlineData(DeviceCommandState.Completed, null, "idle")]
    public async Task PlaybackCompletionReportsAudioAvailability(
        DeviceCommandState finalState,
        string? diagnosticCode,
        string expectedAudioState)
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"edge-agent-{Guid.NewGuid():N}.db");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var commandId = Guid.NewGuid();
        EdgeAgentStatusProvider status = new(new StartupTestClock(now));
        status.Update(current => current with { AudioState = "playing", AudioEndpointId = "endpoint-1" });

        await using (SqliteEdgeAgentStore store = new(databasePath, 10))
        {
            await store.InitializeAsync(CancellationToken.None);
            CommandDispatch command = new(1, commandId, Guid.NewGuid(), Guid.NewGuid(), 1,
                DeviceCommandAction.Start, "test_preset", new string('a', 64), now, now.AddMinutes(2), 60, null);
            Assert.Equal(AcceptCommandResult.Accepted,
                await store.TryAcceptAsync(command, new string('b', 64), now, CancellationToken.None));
            await store.TransitionCommandAsync(commandId, DeviceCommandState.Playing, null, now, CancellationToken.None);

            EdgeAgentPlaybackEventHandler handler = new(store, new StartupTestClock(now), status,
                new EdgeAgentMaintenanceCoordinator());
            await handler.OnPlaybackFinishedAsync(commandId, finalState, diagnosticCode, CancellationToken.None);

            Assert.Equal(expectedAudioState, status.Get().AudioState);
            Assert.Equal(diagnosticCode, status.Get().DiagnosticCode);
            Assert.Equal(finalState.ToString(), status.Get().LastCommandState);

            if (expectedAudioState == "unavailable")
            {
                status.Update(current => current with
                {
                    AudioState = EdgeAgentHostedService.ResolveAudioState(
                        current.AudioEndpointId, null, false, current.AudioState)
                });
                Assert.Equal("unavailable", status.Get().AudioState);
            }
        }

        File.Delete(databasePath);
        File.Delete(databasePath + "-shm");
        File.Delete(databasePath + "-wal");
    }

    [Fact]
    public async Task StartupRecoversInterruptedPlaybackBeforePublishingConnectingStatus()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"edge-agent-{Guid.NewGuid():N}.db");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        EdgeAgentStatusProvider status = new(new StartupTestClock(now));
        await using (SqliteEdgeAgentStore store = new(databasePath, 100))
        {
            await store.InitializeAsync(CancellationToken.None);
            var commandId = Guid.NewGuid();
            CommandDispatch command = new(1, commandId, Guid.NewGuid(), Guid.NewGuid(), 1,
                DeviceCommandAction.Start, "test_preset", new string('a', 64), now, now.AddMinutes(2), 60, null);
            Assert.Equal(AcceptCommandResult.Accepted,
                await store.TryAcceptAsync(command, new string('b', 64), now, CancellationToken.None));
            await store.TransitionCommandAsync(commandId, DeviceCommandState.Playing, null, now, CancellationToken.None);

            EdgeAgentStartupCoordinator coordinator = new(store, new StartupTestClock(now),
                CreateOptions(databasePath), status);
            IReadOnlyList<PendingCommandReceipt> recovered = await coordinator.InitializeAsync(CancellationToken.None);

            Assert.Contains(recovered, item => item.CommandId == commandId &&
                item.State == DeviceCommandState.Failed && item.DiagnosticCode == "agent_restart");
            EdgeAgentStatus actual = status.Get();
            Assert.Equal(EdgeAgentConnectionState.Connecting, actual.ConnectionState);
            Assert.Equal("endpoint-1", actual.AudioEndpointId);
            Assert.Equal("unavailable", actual.AudioState);
        }
        File.Delete(databasePath);
        File.Delete(databasePath + "-shm");
        File.Delete(databasePath + "-wal");
    }

    [Fact]
    public async Task StartupFailureMarksDegradedAndRequestsNonzeroProcessExit()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"edge-agent-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        EdgeAgentStatusProvider status = new(new StartupTestClock(DateTimeOffset.UtcNow));
        await using (SqliteEdgeAgentStore store = new(directory, 100))
        {
            StartupTestClock clock = new(DateTimeOffset.UtcNow);
            EdgeAgentOptions options = CreateOptions(directory);
            EdgeAgentStartupCoordinator coordinator = new(store, clock, options, status);
            DeviceCommandProcessor processor = new(store, new StartupTestAssetResolver(),
                new StartupTestAudioPlayer(), clock, TimeSpan.FromMinutes(2), 100);
            DeviceCommandTransport transport = new(options, processor);
            int? requestedExitCode = null;
            using EdgeAgentHostedService hostedService = new(store, coordinator, transport,
                new StartupTestAudioPlayer(), new StartupTestAudioEndpointAvailabilityMonitor(),
                options, clock, status, NullLogger<EdgeAgentHostedService>.Instance,
                exitCode => requestedExitCode = exitCode);
            await hostedService.StartAsync(CancellationToken.None);
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => hostedService.ExecuteTask!);
            Assert.Equal("Службу Edge Agent зупинено: agent_start_failed.", exception.Message);
            Assert.Equal(1, requestedExitCode);

            Assert.Equal(EdgeAgentConnectionState.Degraded, status.Get().ConnectionState);
            Assert.Equal("agent_start_failed", status.Get().DiagnosticCode);
        }
        Directory.Delete(directory, recursive: true);
    }

    private static EdgeAgentOptions CreateOptions(string databasePath) => new()
    {
        BaseAddress = new Uri("https://watcher.example.invalid"),
        IdentityPath = "/api/v1/device/identity",
        WebSocketPath = "/api/v1/device/commands/stream",
        ReceiptPath = "/api/v1/device/command-receipts",
        DatabasePath = databasePath,
        AudioRootDirectory = Path.GetTempPath(),
        AudioEndpointId = "endpoint-1"
    };

    private sealed class StartupTestClock(DateTimeOffset now) : IEdgeAgentClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }

    private sealed class StartupTestAssetResolver : IAudioAssetResolver
    {
        public Task<LocalAudioAsset?> ResolveAsync(Guid locationId, string presetCode, string manifestHash,
            CancellationToken cancellationToken) => Task.FromResult<LocalAudioAsset?>(null);
    }

    private sealed class StartupTestAudioPlayer : IAudioPlayer
    {
        public Guid? ActiveCommandId => null;
        public Task StartAsync(PlaybackRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> StopAsync(Guid targetCommandId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class StartupTestAudioEndpointAvailabilityMonitor : IAudioEndpointAvailabilityMonitor
    {
        public bool IsAvailable(string? endpointId) => !string.IsNullOrWhiteSpace(endpointId);
    }
}

public sealed class DevicePrivateKeyAccessValidatorTests
{
    [Fact]
    public void ValidatesAUsableLocalPrivateKeyWithoutExportingIt()
    {
        using var rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=KGTA Edge Agent Test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));

        DevicePrivateKeyAccessValidator.Validate(certificate);
    }

    [Fact]
    public void RejectsCertificateWithoutAccessiblePrivateKeyWithSafeDiagnostic()
    {
        using var rsa = RSA.Create(2048);
        CertificateRequest request = new("CN=KGTA Edge Agent Test", rsa, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificateWithKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        using X509Certificate2 certificateWithoutKey = X509CertificateLoader.LoadCertificate(certificateWithKey.RawData);

        EdgeAgentTransportException exception = Assert.Throws<EdgeAgentTransportException>(
            () => DevicePrivateKeyAccessValidator.Validate(certificateWithoutKey));
        Assert.Equal("device_certificate_unavailable", exception.DiagnosticCode);
    }
}
