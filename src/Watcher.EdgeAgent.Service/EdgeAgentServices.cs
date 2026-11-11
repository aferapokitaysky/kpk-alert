using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.Service;

internal sealed class EdgeAgentStatusProvider
{
    private readonly IEdgeAgentClock _clock;
    private readonly object _gate = new();
    private readonly DateTimeOffset _lastStartedAtUtc;
    private EdgeAgentConnectionState _transportState = EdgeAgentConnectionState.Starting;
    private bool _receiptQueueFull;
    private EdgeAgentStatus _status;

    public EdgeAgentStatusProvider(IEdgeAgentClock clock)
    {
        _clock = clock;
        _lastStartedAtUtc = clock.UtcNow;
        _status = new(1, clock.UtcNow, _lastStartedAtUtc,
            EdgeAgentConnectionState.Starting, null, null, "unknown", null, null, null, null, 0, null);
    }

    public EdgeAgentStatus Get() { lock (_gate) return _status; }

    public void Update(Func<EdgeAgentStatus, EdgeAgentStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status) with { ObservedAtUtc = _clock.UtcNow };
        }
    }

    public void ReportDiagnostic(string diagnosticCode)
    {
        lock (_gate)
        {
            _receiptQueueFull = diagnosticCode == "receipt_queue_full" || _receiptQueueFull;
            if (diagnosticCode != "receipt_queue_full")
            {
                _transportState = EdgeAgentConnectionState.Reconnecting;
            }

            _status = _status with
            {
                ConnectionState = _receiptQueueFull ? EdgeAgentConnectionState.Degraded : _transportState,
                DiagnosticCode = diagnosticCode,
                ObservedAtUtc = _clock.UtcNow
            };
        }
    }

    public void UpdateTransportState(EdgeAgentConnectionState state)
    {
        lock (_gate)
        {
            _transportState = state;
            _status = _status with
            {
                ConnectionState = _receiptQueueFull ? EdgeAgentConnectionState.Degraded : state,
                DiagnosticCode = state == EdgeAgentConnectionState.Streaming && !_receiptQueueFull
                    ? null
                    : _status.DiagnosticCode,
                ObservedAtUtc = _clock.UtcNow
            };
        }
    }

    public void UpdatePendingReceiptCount(int pendingCount, int maximumReceiptQueue)
    {
        lock (_gate)
        {
            _receiptQueueFull = pendingCount > maximumReceiptQueue - EdgeAgentOptions.StartCommandReceiptCapacity;
            _status = _status with
            {
                PendingReceiptCount = pendingCount,
                ConnectionState = _receiptQueueFull ? EdgeAgentConnectionState.Degraded : _transportState,
                DiagnosticCode = _receiptQueueFull
                    ? "receipt_queue_full"
                    : _status.DiagnosticCode == "receipt_queue_full" ? null : _status.DiagnosticCode,
                ObservedAtUtc = _clock.UtcNow
            };
        }
    }
}

internal sealed class EdgeAgentPlaybackEventHandler(
    IEdgeAgentStore store,
    IEdgeAgentClock clock,
    EdgeAgentStatusProvider status,
    EdgeAgentMaintenanceCoordinator maintenanceCoordinator) : IAudioPlaybackEventHandler
{
    public async Task OnPlaybackFinishedAsync(Guid commandId, DeviceCommandState state,
        string? diagnosticCode, CancellationToken cancellationToken)
    {
        try
        {
            ProcessedCommand? command = await store.FindCommandAsync(commandId, cancellationToken);
            if (command is null) return;
            await store.TransitionCommandAsync(commandId, state, diagnosticCode, clock.UtcNow, cancellationToken);
            status.Update(current => current with
            {
                AudioState = diagnosticCode == "audio_output_unavailable" ? "unavailable" : "idle",
                LastCommandId = commandId,
                LastSequence = command.Sequence,
                LastCommandState = state.ToString(),
                DiagnosticCode = diagnosticCode
            });
        }
        finally
        {
            maintenanceCoordinator.PlaybackFinished(commandId);
        }
    }
}

internal sealed class EdgeAgentHostedService(
    IEdgeAgentStore store,
    EdgeAgentStartupCoordinator startupCoordinator,
    DeviceCommandTransport transport,
    IAudioPlayer audioPlayer,
    IAudioEndpointAvailabilityMonitor audioEndpointAvailability,
    EdgeAgentOptions options,
    IEdgeAgentClock clock,
    EdgeAgentStatusProvider status,
    ILogger<EdgeAgentHostedService> logger,
    Action<int>? terminateProcess = null) : BackgroundService
{
    private readonly Action<int> _terminateProcess = terminateProcess ?? Environment.Exit;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await startupCoordinator.InitializeAsync(stoppingToken);
            using X509Certificate2 certificate = DeviceCertificateStore.FindClientCertificate(options.ClientCertificateThumbprint!);
            DevicePrivateKeyAccessValidator.Validate(certificate);
            using HttpClient httpClient = MtlsHttpClientFactory.Create(options, certificate);
            var receipts = new CommandReceiptSender(options, store, httpClient, clock);
            using var workers = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            Task[] workerTasks =
            [
                receipts.RunAsync(workers.Token),
                transport.RunAsync(workers.Token),
                RefreshStatusAsync(workers.Token)
            ];
            await Task.WhenAny(workerTasks);
            if (stoppingToken.IsCancellationRequested)
            {
                await Task.WhenAll(workerTasks);
                return;
            }

            workers.Cancel();
            await Task.WhenAll(workerTasks);
            throw new InvalidOperationException("Фоновий процес Edge Agent зупинився несподівано.");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            var diagnosticCode = exception switch
            {
                EdgeAgentTransportException transportException => transportException.DiagnosticCode,
                AuthenticationException or CryptographicException => "device_certificate_unavailable",
                _ => "agent_start_failed"
            };
            startupCoordinator.MarkDegraded(diagnosticCode);
            logger.LogError("Edge Agent зупинився: {DiagnosticCode}.", diagnosticCode);
            _terminateProcess(1);
            throw new InvalidOperationException($"Службу Edge Agent зупинено: {diagnosticCode}.");
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        long? lastAudioEndpointProbeTimestamp = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var pendingCount = await store.GetPendingReceiptCountAsync(cancellationToken);
            Guid? activeCommandId = audioPlayer.ActiveCommandId;
            ProcessedCommand? activeCommand = activeCommandId is { } commandId
                ? await store.FindCommandAsync(commandId, cancellationToken)
                : null;
            var probeAudioEndpoint = lastAudioEndpointProbeTimestamp is null ||
                clock.GetElapsedTime(lastAudioEndpointProbeTimestamp.Value) >= TimeSpan.FromSeconds(5);
            if (probeAudioEndpoint)
            {
                lastAudioEndpointProbeTimestamp = clock.GetTimestamp();
            }

            var endpointAvailable = probeAudioEndpoint && audioEndpointAvailability.IsAvailable(status.Get().AudioEndpointId);
            status.Update(current => current with
            {
                AudioState = activeCommand is not null
                    ? "playing"
                    : probeAudioEndpoint
                        ? ResolveAudioState(current.AudioEndpointId, null, endpointAvailable, current.AudioState)
                        : current.AudioState,
                LastCommandId = activeCommand?.CommandId ?? current.LastCommandId,
                LastSequence = activeCommand?.Sequence ?? current.LastSequence,
                LastCommandState = activeCommand?.State.ToString() ?? current.LastCommandState
            });
            status.UpdatePendingReceiptCount(pendingCount, options.MaximumReceiptQueue);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    internal static string ResolveAudioState(
        string? audioEndpointId,
        ProcessedCommand? activeCommand,
        bool endpointAvailable,
        string currentState) =>
        activeCommand is not null
            ? "playing"
            : string.IsNullOrWhiteSpace(audioEndpointId) || !endpointAvailable
                ? "unavailable"
                : currentState == "idle" ? "idle" : "ready";
}

internal sealed class EdgeAgentStartupCoordinator(
    IEdgeAgentStore store,
    IEdgeAgentClock clock,
    EdgeAgentOptions options,
    EdgeAgentStatusProvider status)
{
    public async Task<IReadOnlyList<PendingCommandReceipt>> InitializeAsync(CancellationToken cancellationToken)
    {
        await store.InitializeAsync(cancellationToken);
        IReadOnlyList<PendingCommandReceipt> recoveredReceipts =
            await store.RecoverInterruptedPlaybackAsync(clock.UtcNow, cancellationToken);
        status.Update(current => current with
        {
            ConnectionState = EdgeAgentConnectionState.Connecting,
            AudioEndpointId = options.AudioEndpointId,
            AudioState = "unavailable"
        });
        return recoveredReceipts;
    }

    public void MarkDegraded(string diagnosticCode) => status.Update(current => current with
    {
        ConnectionState = EdgeAgentConnectionState.Degraded,
        DiagnosticCode = diagnosticCode
    });
}

internal sealed class EdgeAgentStatusPipeService(
    EdgeAgentStatusProvider status,
    ILogger<EdgeAgentStatusPipeService> logger,
    string pipeName = "KGTA.Alert.EdgeAgent.Status.v1",
    Func<SecurityIdentifier>? serviceSidProvider = null,
    Func<SecurityIdentifier>? operatorGroupSidProvider = null,
    EdgeAgentMaintenanceCoordinator? maintenanceCoordinator = null,
    EdgeAgentDatabaseBackup? databaseBackup = null) : BackgroundService
{
    private const int MaximumFrameBytes = 4096;
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaintenanceDrainTimeout = TimeSpan.FromMinutes(15);
    private readonly object _maintenanceLeaseSync = new();
    private Guid? _maintenanceLeaseId;
    private string? _maintenanceLeaseOwnerSid;
    private readonly EdgeAgentMaintenanceCoordinator _maintenanceCoordinator =
        maintenanceCoordinator ?? new EdgeAgentMaintenanceCoordinator();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            using NamedPipeServerStream pipe = CreatePipe(pipeName, serviceSidProvider, operatorGroupSidProvider);
            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                readTimeout.CancelAfter(ClientRequestTimeout);
                var requestBytes = await ReadFrameAsync(pipe, readTimeout.Token);
                EdgeAgentPipeRequest? request = JsonSerializer.Deserialize<EdgeAgentPipeRequest>(requestBytes, JsonOptions);
                using var operationTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                operationTimeout.CancelAfter(MaintenanceDrainTimeout);
                string? callerSid = null;
                var isMaintenanceRequest = request?.Operation is "begin_maintenance" or "release_maintenance" or "backup_database";
                var maintenanceCallerAuthorized = !isMaintenanceRequest || TryGetAuthorizedMaintenanceCaller(pipe, out callerSid);
                Task? disconnectMonitor = null;
                using var disconnectMonitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(operationTimeout.Token);
                if (request?.Operation == "begin_maintenance" && maintenanceCallerAuthorized)
                {
                    disconnectMonitor = CancelOperationWhenClientDisconnectsAsync(
                        pipe, operationTimeout, disconnectMonitorCancellation.Token);
                }

                EdgeAgentPipeResponse response;
                try
                {
                    response = request is null
                        ? new EdgeAgentPipeResponse(1, false, null, "invalid_request")
                        : !maintenanceCallerAuthorized
                            ? new EdgeAgentPipeResponse(1, false, null, "maintenance_access_denied")
                            : await HandleRequestAsync(request, callerSid, operationTimeout.Token);
                }
                finally
                {
                    disconnectMonitorCancellation.Cancel();
                    if (disconnectMonitor is not null)
                    {
                        await disconnectMonitor;
                    }
                }

                using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                writeTimeout.CancelAfter(ClientRequestTimeout);
                await WriteResponseAsync(pipe, response, writeTimeout.Token);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException)
            {
                logger.LogWarning("Вичерпано час очікування запиту локальної панелі стану.");
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
            {
                logger.LogWarning("Відхилено запит локальної панелі стану: {ExceptionType}.", exception.GetType().Name);
            }
        }
    }

    internal async Task<EdgeAgentPipeResponse> HandleRequestAsync(
        EdgeAgentPipeRequest request,
        string? callerSid,
        CancellationToken cancellationToken)
    {
        if (request.SchemaVersion != 1)
        {
            return new EdgeAgentPipeResponse(1, false, null, "invalid_request");
        }

        switch (request.Operation)
        {
            case "get_status" when request.LeaseId is null:
                return new EdgeAgentPipeResponse(1, true, status.Get(), null);

            case "begin_maintenance" when request.LeaseId is null:
                if (string.IsNullOrWhiteSpace(callerSid))
                {
                    return new EdgeAgentPipeResponse(1, false, null, "maintenance_access_denied");
                }

                lock (_maintenanceLeaseSync)
                {
                    if (_maintenanceLeaseId is { } previousLeaseId &&
                        !_maintenanceCoordinator.IsMaintenanceLeaseActive(previousLeaseId))
                    {
                        _maintenanceLeaseId = null;
                        _maintenanceLeaseOwnerSid = null;
                    }
                }

                try
                {
                    Guid leaseId = await _maintenanceCoordinator.BeginMaintenanceAsync(
                        MaintenanceDrainTimeout, cancellationToken);
                    lock (_maintenanceLeaseSync)
                    {
                        _maintenanceLeaseId = leaseId;
                        _maintenanceLeaseOwnerSid = callerSid;
                    }

                    return new EdgeAgentPipeResponse(1, true, status.Get(), null, leaseId);
                }
                catch (InvalidOperationException)
                {
                    return new EdgeAgentPipeResponse(1, false, null, "maintenance_already_active");
                }
                catch (TimeoutException)
                {
                    return new EdgeAgentPipeResponse(1, false, null, "maintenance_drain_timeout");
                }

            case "release_maintenance" when request.LeaseId is { } leaseId:
                if (string.IsNullOrWhiteSpace(callerSid))
                {
                    return new EdgeAgentPipeResponse(1, false, null, "maintenance_access_denied");
                }

                lock (_maintenanceLeaseSync)
                {
                    if (_maintenanceLeaseId != leaseId)
                    {
                        return new EdgeAgentPipeResponse(1, false, null, "maintenance_lease_invalid");
                    }

                    if (!string.Equals(_maintenanceLeaseOwnerSid, callerSid, StringComparison.OrdinalIgnoreCase))
                    {
                        return new EdgeAgentPipeResponse(1, false, null, "maintenance_lease_owner_mismatch");
                    }

                    var released = _maintenanceCoordinator.ReleaseMaintenance(leaseId);
                    _maintenanceLeaseId = null;
                    _maintenanceLeaseOwnerSid = null;
                    return released
                        ? new EdgeAgentPipeResponse(1, true, status.Get(), null)
                        : new EdgeAgentPipeResponse(1, false, null, "maintenance_lease_invalid");
                }

            case "backup_database" when request.LeaseId is { } backupLeaseId:
                if (string.IsNullOrWhiteSpace(callerSid))
                {
                    return new EdgeAgentPipeResponse(1, false, null, "maintenance_access_denied");
                }

                IDisposable? leaseKeepAlive;
                lock (_maintenanceLeaseSync)
                {
                    if (_maintenanceLeaseId != backupLeaseId)
                    {
                        return new EdgeAgentPipeResponse(1, false, null, "maintenance_lease_invalid");
                    }

                    if (!string.Equals(_maintenanceLeaseOwnerSid, callerSid, StringComparison.OrdinalIgnoreCase))
                    {
                        return new EdgeAgentPipeResponse(1, false, null, "maintenance_lease_owner_mismatch");
                    }

                    leaseKeepAlive = _maintenanceCoordinator.KeepMaintenanceLeaseAlive(backupLeaseId);
                }

                if (leaseKeepAlive is null)
                {
                    return new EdgeAgentPipeResponse(1, false, null, "maintenance_lease_invalid");
                }

                using (leaseKeepAlive)
                {
                    if (databaseBackup is null)
                    {
                        return new EdgeAgentPipeResponse(1, false, null, "database_backup_unavailable");
                    }

                    try
                    {
                        EdgeAgentDatabaseBackupResult backup = databaseBackup.Create();
                        return new EdgeAgentPipeResponse(
                            1, true, status.Get(), null, BackupId: backup.BackupId,
                            DatabasePathHash: backup.DatabasePathHash, MaintenanceLeaseId: backupLeaseId);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogWarning("Не вдалося створити резервну копію бази Edge Agent: {ExceptionType}.",
                            exception.GetType().Name);
                        return new EdgeAgentPipeResponse(
                            1, false, null, "database_backup_failed", MaintenanceLeaseId: backupLeaseId);
                    }
                }

            default:
                return new EdgeAgentPipeResponse(1, false, null, "invalid_request");
        }
    }

    internal async Task WriteResponseAsync(
        Stream pipe,
        EdgeAgentPipeResponse response,
        CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
            if (payload.Length > MaximumFrameBytes)
            {
                throw new InvalidDataException("Відповідь каналу стану завелика.");
            }

            await WriteFrameAsync(pipe, payload, cancellationToken);
        }
        catch
        {
            if (response.LeaseId is { } leaseId)
            {
                _maintenanceCoordinator.ReleaseMaintenance(leaseId);
                lock (_maintenanceLeaseSync)
                {
                    if (_maintenanceLeaseId == leaseId)
                    {
                        _maintenanceLeaseId = null;
                        _maintenanceLeaseOwnerSid = null;
                    }
                }
            }

            throw;
        }
    }

    private static bool TryGetAuthorizedMaintenanceCaller(NamedPipeServerStream pipe, out string? callerSid)
    {
        string? resolvedCallerSid = null;
        var authorized = false;
        try
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                if (identity.User is null)
                {
                    return;
                }

                resolvedCallerSid = identity.User.Value;
                SecurityIdentifier administratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
                var serviceSid = (SecurityIdentifier)new NTAccount(
                    "NT SERVICE", "KGTA.Alert.EdgeAgent").Translate(typeof(SecurityIdentifier));
                authorized = identity.IsSystem || identity.User.Equals(serviceSid) ||
                    new WindowsPrincipal(identity).IsInRole(administratorsSid);
            });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException or InvalidOperationException)
        {
            callerSid = null;
            return false;
        }

        callerSid = resolvedCallerSid;
        return authorized;
    }

    private static async Task CancelOperationWhenClientDisconnectsAsync(
        Stream pipe,
        CancellationTokenSource operationCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            var unexpectedData = new byte[1];
            await ReadExactlyAsync(pipe, unexpectedData, cancellationToken);
            operationCancellation.Cancel();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            operationCancellation.Cancel();
        }
    }

    private static NamedPipeServerStream CreatePipe(
        string pipeName,
        Func<SecurityIdentifier>? serviceSidProvider,
        Func<SecurityIdentifier>? operatorGroupSidProvider = null)
    {
        PipeSecurity security = CreatePipeSecurity(serviceSidProvider, operatorGroupSidProvider);
        var securityDescriptor = security.GetSecurityDescriptorBinaryForm();
        using var descriptor = new SafeLocalAllocHandle(Marshal.AllocHGlobal(securityDescriptor.Length));
        Marshal.Copy(securityDescriptor, 0, descriptor.DangerousGetHandle(), securityDescriptor.Length);
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            SecurityDescriptor = descriptor.DangerousGetHandle(),
            InheritHandle = false
        };

        SafePipeHandle pipeHandle = CreateNamedPipe(
            $"\\\\.\\pipe\\{pipeName}",
            PipeAccessDuplex | FileFlagOverlapped,
            CreatePipeMode(),
            1,
            4096,
            4096,
            0,
            ref attributes);
        if (pipeHandle.IsInvalid)
        {
            var errorCode = Marshal.GetLastWin32Error();
            pipeHandle.Dispose();
            throw new Win32Exception(errorCode, "Не вдалося створити локальний status pipe.");
        }

        try
        {
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, pipeHandle);
        }
        catch
        {
            pipeHandle.Dispose();
            throw;
        }
    }

    internal static uint CreatePipeMode() => PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients;

    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool InheritHandle;
    }

    private sealed class SafeLocalAllocHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeLocalAllocHandle(IntPtr value) : base(true)
        {
            SetHandle(value);
        }

        public override bool IsInvalid => IsClosed || handle == IntPtr.Zero || handle == new IntPtr(-1);

        protected override bool ReleaseHandle()
        {
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maxInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

    internal static PipeSecurity CreatePipeSecurity(
        Func<SecurityIdentifier>? serviceSidProvider,
        Func<SecurityIdentifier>? operatorGroupSidProvider)
    {
        PipeSecurity security = new();
        SecurityIdentifier serviceSid = serviceSidProvider is null
            ? (SecurityIdentifier)new NTAccount("NT SERVICE\\KGTA.Alert.EdgeAgent").Translate(typeof(SecurityIdentifier))
            : serviceSidProvider();
        SecurityIdentifier administratorsSid = new(WellKnownSidType.BuiltinAdministratorsSid, null);
        SecurityIdentifier operatorsSid = operatorGroupSidProvider is null
            ? (SecurityIdentifier)new NTAccount(Environment.MachineName, "KGTA Alert Operators")
                .Translate(typeof(SecurityIdentifier))
            : operatorGroupSidProvider();
        security.AddAccessRule(new PipeAccessRule(serviceSid,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(administratorsSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(operatorsSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return security;
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaximumFrameBytes) throw new InvalidDataException("Некоректний розмір запиту.");
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }
}
