using System.Net.WebSockets;

namespace Watcher.Api.AlertSources;

internal sealed class NeptunRealtimeMonitor(
    NeptunProvider provider,
    INeptunRealtimeConnectionFactory connectionFactory,
    NeptunRealtimeOptions options,
    TimeProvider timeProvider,
    ILogger<NeptunRealtimeMonitor> logger) : BackgroundService
{
    private static readonly Uri StreamUri = new("wss://neptun.in.ua/api/v1/stream");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        provider.EnableRealtime();
        DateTimeOffset lastRestAttempt = DateTimeOffset.MinValue;
        NeptunReconnectBackoff reconnectBackoff = new(
            options.InitialReconnectDelay,
            options.MaximumReconnectDelay);

        while (!stoppingToken.IsCancellationRequested)
        {
            NeptunConnectionReadResult readResult = new(ShouldReconnect: true, HadValidAlertActivity: false);
            try
            {
                await RefreshFromRestIfDueAsync(force: lastRestAttempt == DateTimeOffset.MinValue, stoppingToken);
                await using INeptunRealtimeConnection connection = await connectionFactory.ConnectAsync(
                    StreamUri,
                    stoppingToken);
                readResult = await ReadConnectionAsync(connection, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is WebSocketException or HttpRequestException or InvalidDataException)
            {
                logger.LogWarning(exception, "З’єднання з потоком NEPTUN перервано.");
                if (exception is InvalidDataException)
                {
                    provider.InvalidateRealtimeSnapshot("invalid_response");
                }
                else
                {
                    provider.MarkRealtimeUnavailable("source_unavailable");
                }
            }

            if (!readResult.ShouldReconnect)
            {
                continue;
            }

            await RefreshFromRestIfDueAsync(force: false, stoppingToken);
            await Task.Delay(
                reconnectBackoff.GetNextDelay(readResult.HadValidAlertActivity),
                timeProvider,
                stoppingToken);
        }

        async Task RefreshFromRestIfDueAsync(bool force, CancellationToken cancellationToken)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            if (!force && now - lastRestAttempt < options.RestFallbackInterval)
            {
                return;
            }

            lastRestAttempt = now;
            await provider.RefreshFromRestAsync(cancellationToken, allowRealtimeStateUpdate: true);
        }
    }

    private async Task<NeptunConnectionReadResult> ReadConnectionAsync(
        INeptunRealtimeConnection connection,
        CancellationToken cancellationToken)
    {
        DateTimeOffset lastHeartbeat = timeProvider.GetUtcNow();
        var hadValidAlertActivity = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan remaining = options.HeartbeatTimeout - (timeProvider.GetUtcNow() - lastHeartbeat);
            if (remaining <= TimeSpan.Zero)
            {
                provider.MarkRealtimeUnavailable("heartbeat_timeout");
                return new(ShouldReconnect: true, hadValidAlertActivity);
            }

            using var receiveTimeout =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            receiveTimeout.CancelAfter(remaining);
            NeptunRealtimeMessage message;
            try
            {
                message = await connection.ReceiveAsync(options.MaximumMessageBytes, receiveTimeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                provider.MarkRealtimeUnavailable("heartbeat_timeout");
                return new(ShouldReconnect: true, hadValidAlertActivity);
            }

            if (message.MessageType == WebSocketMessageType.Close)
            {
                provider.MarkRealtimeUnavailable("source_unavailable");
                return new(ShouldReconnect: true, hadValidAlertActivity);
            }

            if (message.MessageType != WebSocketMessageType.Text)
            {
                provider.InvalidateRealtimeSnapshot("invalid_response");
                return new(ShouldReconnect: true, hadValidAlertActivity);
            }

            NeptunRealtimeFrame frame = NeptunAlertSnapshotParser.ParseRealtimeFrame(message.Payload);
            switch (frame.Kind)
            {
                case NeptunRealtimeFrameKind.Alerts when frame.AlertSnapshot is not null:
                    provider.ApplyRealtimeSnapshot(frame.AlertSnapshot.Value);
                    lastHeartbeat = timeProvider.GetUtcNow();
                    hadValidAlertActivity = true;
                    break;
                case NeptunRealtimeFrameKind.Heartbeat:
                    lastHeartbeat = timeProvider.GetUtcNow();
                    hadValidAlertActivity = true;
                    break;
                case NeptunRealtimeFrameKind.Snapshot:
                case NeptunRealtimeFrameKind.Ignored:
                    break;
                default:
                    provider.InvalidateRealtimeSnapshot("invalid_response");
                    return new(ShouldReconnect: true, hadValidAlertActivity);
            }
        }

        return new(ShouldReconnect: false, hadValidAlertActivity);
    }
}

internal readonly record struct NeptunConnectionReadResult(
    bool ShouldReconnect,
    bool HadValidAlertActivity);
