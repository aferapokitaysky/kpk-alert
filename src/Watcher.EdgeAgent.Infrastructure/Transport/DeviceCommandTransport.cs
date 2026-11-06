using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Transport;

public sealed class DeviceCommandTransport(
    EdgeAgentOptions options,
    DeviceCommandProcessor processor,
    Func<string, Exception?, ValueTask>? reportDiagnostic = null,
    Func<DeviceIdentity, ValueTask>? identityObserved = null,
    Func<EdgeAgentConnectionState, ValueTask>? stateChanged = null)
{
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public async Task<DeviceIdentity> GetIdentityAsync(
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            new Uri(options.BaseAddress, options.IdentityPath),
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new EdgeAgentTransportException("identity_request_failed");
        }

        await using Stream responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var responseBytes = await ReadBoundedAsync(responseStream, 8 * 1024, cancellationToken);
        DeviceIdentityResponse? result;
        try
        {
            result = JsonSerializer.Deserialize<DeviceIdentityResponse>(responseBytes, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new EdgeAgentTransportException("identity_response_invalid", exception);
        }
        if (result is null || result.DeviceId == Guid.Empty || result.LocationId == Guid.Empty)
        {
            throw new EdgeAgentTransportException("identity_response_invalid");
        }

        return new DeviceIdentity(result.DeviceId, result.LocationId);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Validate() is not null)
        {
            throw new EdgeAgentTransportException("invalid_configuration");
        }

        if (string.IsNullOrWhiteSpace(options.ClientCertificateThumbprint))
        {
            throw new EdgeAgentTransportException("device_certificate_unavailable");
        }

        using X509Certificate2 certificate = DeviceCertificateStore.FindClientCertificate(options.ClientCertificateThumbprint);
        using HttpClient httpClient = MtlsHttpClientFactory.Create(options, certificate);
        var reconnectAttempt = 0;
        await ReportStateAsync(EdgeAgentConnectionState.Connecting);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DeviceIdentity identity;
                using (var identityTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    identityTimeout.CancelAfter(ConnectionTimeout);
                    identity = await GetIdentityAsync(httpClient, identityTimeout.Token);
                }

                await ObserveIdentityAsync(identity);
                await RunConnectionAsync(certificate, identity, cancellationToken);
                reconnectAttempt = 0;
                await ReportAsync("device_stream_closed", null);
                await ReportStateAsync(EdgeAgentConnectionState.Reconnecting);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (
                IsExpectedTransportFailure(exception) ||
                exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                await ReportAsync(GetDiagnosticCode(exception), exception);
                await ReportStateAsync(EdgeAgentConnectionState.Reconnecting);
            }

            TimeSpan delay = GetReconnectDelay(reconnectAttempt++,
                TimeSpan.FromSeconds(options.ReconnectInitialDelaySeconds),
                TimeSpan.FromSeconds(options.ReconnectMaximumDelaySeconds));
            await Task.Delay(delay, cancellationToken);
        }
    }

    private async Task RunConnectionAsync(
        System.Security.Cryptography.X509Certificates.X509Certificate2 certificate,
        DeviceIdentity identity,
        CancellationToken cancellationToken)
    {
        using ClientWebSocket socket = new();
        socket.Options.ClientCertificates = new System.Security.Cryptography.X509Certificates.X509CertificateCollection { certificate };
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        Uri streamUri = BuildWebSocketUri(options.BaseAddress, options.WebSocketPath);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            connectTimeout.CancelAfter(ConnectionTimeout);
            await socket.ConnectAsync(streamUri, connectTimeout.Token);
        }
        await ReportStateAsync(EdgeAgentConnectionState.Streaming);

        var buffer = new byte[Math.Min(4096, options.MaximumWebSocketMessageBytes)];
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var payload = await ReceiveTextMessageAsync(
                socket, buffer, options.MaximumWebSocketMessageBytes, cancellationToken);
            CommandDispatch? command;
            try
            {
                command = JsonSerializer.Deserialize<CommandDispatch>(payload, JsonOptions);
            }
            catch (JsonException exception)
            {
                await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, cancellationToken);
                throw new EdgeAgentTransportException("command_payload_invalid", exception);
            }

            if (command is null)
            {
                await CloseAsync(socket, WebSocketCloseStatus.InvalidPayloadData, cancellationToken);
                throw new EdgeAgentTransportException("command_payload_invalid");
            }

            CommandProcessingOutcome outcome = await processor.ProcessAsync(command, identity, cancellationToken);
            await ReportProcessingOutcomeAsync(outcome);
            if (outcome.CloseConnection)
            {
                WebSocketCloseStatus closeStatus = outcome.DiagnosticCode is "stop_attempt_failed" or
                    "stop_target_pending" or "command_state_persistence_failed"
                    ? WebSocketCloseStatus.InternalServerError
                    : WebSocketCloseStatus.PolicyViolation;
                await CloseAsync(socket, closeStatus, cancellationToken);
                throw new EdgeAgentTransportException(outcome.DiagnosticCode ?? "command_contract_conflict");
            }
        }
    }

    internal ValueTask ReportProcessingOutcomeAsync(CommandProcessingOutcome outcome) =>
        outcome.DiagnosticCode == "receipt_queue_full"
            ? ReportAsync(outcome.DiagnosticCode, null)
            : ValueTask.CompletedTask;

    private static async Task<byte[]> ReceiveTextMessageAsync(
        ClientWebSocket socket,
        byte[] buffer,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream message = new();
        WebSocketMessageType? messageType = null;
        while (true)
        {
            ValueWebSocketReceiveResult result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new EdgeAgentTransportException("device_stream_closed");
            }

            messageType ??= result.MessageType;
            if (result.MessageType != messageType || result.MessageType != WebSocketMessageType.Text)
            {
                await CloseAsync(socket, WebSocketCloseStatus.InvalidMessageType, cancellationToken);
                throw new EdgeAgentTransportException("command_message_type_invalid");
            }

            if (message.Length + result.Count > maximumBytes)
            {
                await CloseAsync(socket, WebSocketCloseStatus.MessageTooBig, cancellationToken);
                throw new EdgeAgentTransportException("command_message_too_large");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return message.ToArray();
            }
        }
    }

    private static async Task CloseAsync(
        ClientWebSocket socket,
        WebSocketCloseStatus status,
        CancellationToken cancellationToken)
    {
        if (socket.State == WebSocketState.Open)
        {
            await socket.CloseAsync(status, "Invalid device command stream message.", cancellationToken);
        }
    }

    private async ValueTask ReportAsync(string code, Exception? exception)
    {
        if (reportDiagnostic is not null)
        {
            try { await reportDiagnostic(code, exception); }
            catch (Exception callbackException)
            {
                System.Diagnostics.Trace.TraceError("Не вдалося оновити діагностику Edge Agent: {0}", callbackException.GetType().Name);
            }
        }
    }

    private async ValueTask ReportStateAsync(EdgeAgentConnectionState state)
    {
        if (stateChanged is null) return;
        try { await stateChanged(state); }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Спостерігач стану Edge Agent завершився помилкою: {0}", exception.GetType().Name);
            await ReportAsync("status_observer_failed", exception);
        }
    }

    private async ValueTask ObserveIdentityAsync(DeviceIdentity identity)
    {
        if (identityObserved is null) return;
        try { await identityObserved(identity); }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError("Не вдалося оновити ідентичність вузла в стані: {0}", exception.GetType().Name);
            await ReportAsync("status_observer_failed", exception);
        }
    }

    private TimeSpan GetReconnectDelay(int attempt, TimeSpan initial, TimeSpan maximum) =>
        GetRetryDelay(attempt, initial, maximum, Random.Shared.NextDouble());

    internal static TimeSpan GetRetryDelay(int attempt, TimeSpan initial, TimeSpan maximum, double jitterFactor)
    {
        var exponent = Math.Clamp(attempt, 0, 30);
        var rawMilliseconds = Math.Min(maximum.TotalMilliseconds, initial.TotalMilliseconds * Math.Pow(2, exponent));
        var jitter = Math.Clamp(jitterFactor, 0.75, 1.25);
        return TimeSpan.FromMilliseconds(Math.Min(maximum.TotalMilliseconds, rawMilliseconds * jitter));
    }

    private static bool IsExpectedTransportFailure(Exception exception) =>
        exception is HttpRequestException or WebSocketException or JsonException or EdgeAgentTransportException or IOException or TimeoutException;

    private static string GetDiagnosticCode(Exception exception) => exception is EdgeAgentTransportException transport
        ? transport.DiagnosticCode
        : "device_transport_unavailable";

    private static Uri BuildWebSocketUri(Uri baseAddress, string path)
    {
        UriBuilder builder = new(new Uri(baseAddress, path))
        {
            Scheme = baseAddress.Scheme == Uri.UriSchemeHttps ? "wss" : "ws"
        };
        return builder.Uri;
    }

    private static async Task<byte[]> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        var chunk = new byte[1024];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new EdgeAgentTransportException("identity_response_too_large");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private sealed record DeviceIdentityResponse(Guid DeviceId, Guid LocationId);
}
