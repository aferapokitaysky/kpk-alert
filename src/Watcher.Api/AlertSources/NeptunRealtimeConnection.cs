using System.Net.WebSockets;

namespace Watcher.Api.AlertSources;

internal interface INeptunRealtimeConnectionFactory
{
    Task<INeptunRealtimeConnection> ConnectAsync(Uri streamUri, CancellationToken cancellationToken);
}

internal interface INeptunRealtimeConnection : IAsyncDisposable
{
    Task<NeptunRealtimeMessage> ReceiveAsync(int maximumMessageBytes, CancellationToken cancellationToken);
}

internal readonly record struct NeptunRealtimeMessage(
    WebSocketMessageType MessageType,
    byte[] Payload,
    WebSocketCloseStatus? CloseStatus = null);

internal sealed class NeptunRealtimeConnectionFactory : INeptunRealtimeConnectionFactory
{
    public async Task<INeptunRealtimeConnection> ConnectAsync(
        Uri streamUri,
        CancellationToken cancellationToken)
    {
        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(streamUri, cancellationToken);
            return new NeptunRealtimeConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal sealed class NeptunRealtimeConnection(ClientWebSocket socket) : INeptunRealtimeConnection
{
    public async Task<NeptunRealtimeMessage> ReceiveAsync(
        int maximumMessageBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream message = new();
        var buffer = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return new(result.MessageType, [], socket.CloseStatus);
            }

            if (message.Length + result.Count > maximumMessageBytes)
            {
                throw new InvalidDataException("Повідомлення NEPTUN перевищує дозволений розмір.");
            }

            message.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return new(result.MessageType, message.ToArray());
    }

    public ValueTask DisposeAsync()
    {
        socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
