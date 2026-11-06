using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.Service;

internal sealed class EdgeAgentStatusProvider(IEdgeAgentClock clock)
{
    private readonly object _gate = new();
    private EdgeAgentConnectionState _transportState = EdgeAgentConnectionState.Starting;
    private bool _receiptQueueFull;
    private EdgeAgentStatus _status = new(1, DateTimeOffset.UtcNow,
        EdgeAgentConnectionState.Starting, null, null, "unknown", null, null, null, null, 0, null);

    public EdgeAgentStatus Get() { lock (_gate) return _status; }

    public void Update(Func<EdgeAgentStatus, EdgeAgentStatus> update)
    {
        lock (_gate)
        {
            _status = update(_status) with { ObservedAtUtc = clock.UtcNow };
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
                ObservedAtUtc = clock.UtcNow
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
                ObservedAtUtc = clock.UtcNow
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
                ObservedAtUtc = clock.UtcNow
            };
        }
    }
}

internal sealed class EdgeAgentPlaybackEventHandler(
    IEdgeAgentStore store,
    IEdgeAgentClock clock,
    EdgeAgentStatusProvider status) : IAudioPlaybackEventHandler
{
    public async Task OnPlaybackFinishedAsync(Guid commandId, DeviceCommandState state,
        string? diagnosticCode, CancellationToken cancellationToken)
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
}

internal sealed class EdgeAgentHostedService(
    IEdgeAgentStore store,
    EdgeAgentStartupCoordinator startupCoordinator,
    DeviceCommandTransport transport,
    IAudioPlayer audioPlayer,
    EdgeAgentOptions options,
    IEdgeAgentClock clock,
    EdgeAgentStatusProvider status,
    ILogger<EdgeAgentHostedService> logger) : BackgroundService
{
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
            Environment.ExitCode = 1;
            throw new InvalidOperationException($"Службу Edge Agent зупинено: {diagnosticCode}.");
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var pendingCount = await store.GetPendingReceiptCountAsync(cancellationToken);
            Guid? activeCommandId = audioPlayer.ActiveCommandId;
            ProcessedCommand? activeCommand = activeCommandId is { } commandId
                ? await store.FindCommandAsync(commandId, cancellationToken)
                : null;
            status.Update(current => current with
            {
                AudioState = ResolveAudioState(current.AudioEndpointId, activeCommand, current.AudioState),
                LastCommandId = activeCommand?.CommandId ?? current.LastCommandId,
                LastSequence = activeCommand?.Sequence ?? current.LastSequence,
                LastCommandState = activeCommand?.State.ToString() ?? current.LastCommandState
            });
            status.UpdatePendingReceiptCount(pendingCount, options.MaximumReceiptQueue);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    internal static string ResolveAudioState(string? audioEndpointId, ProcessedCommand? activeCommand, string currentState) =>
        activeCommand is not null
            ? "playing"
            : currentState == "unavailable"
                ? "unavailable"
            : string.IsNullOrWhiteSpace(audioEndpointId)
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
            AudioState = string.IsNullOrWhiteSpace(options.AudioEndpointId) ? "unavailable" : "ready"
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
    Func<SecurityIdentifier>? operatorGroupSidProvider = null) : BackgroundService
{
    private const int MaximumFrameBytes = 4096;
    private static readonly TimeSpan ClientRequestTimeout = TimeSpan.FromSeconds(5);
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
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                requestTimeout.CancelAfter(ClientRequestTimeout);
                var requestBytes = await ReadFrameAsync(pipe, requestTimeout.Token);
                EdgeAgentPipeRequest? request = JsonSerializer.Deserialize<EdgeAgentPipeRequest>(requestBytes, JsonOptions);
                EdgeAgentPipeResponse response = request is { SchemaVersion: 1, Operation: "get_status" }
                    ? new EdgeAgentPipeResponse(1, true, status.Get(), null)
                    : new EdgeAgentPipeResponse(1, false, null, "invalid_request");
                var payload = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
                if (payload.Length <= MaximumFrameBytes) await WriteFrameAsync(pipe, payload, requestTimeout.Token);
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

    private static NamedPipeServerStream CreatePipe(
        string pipeName,
        Func<SecurityIdentifier>? serviceSidProvider,
        Func<SecurityIdentifier>? operatorGroupSidProvider = null)
    {
        PipeSecurity security = CreatePipeSecurity(serviceSidProvider, operatorGroupSidProvider);
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

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
        security.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
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
