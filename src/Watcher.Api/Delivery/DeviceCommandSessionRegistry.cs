using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Watcher.Api.Delivery;

public sealed class DeviceCommandSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, DeviceCommandWebSocketSession> _sessions = new();
    private readonly object _registrationLock = new();

    public IReadOnlyCollection<Guid> ConnectedDeviceIds => _sessions.Keys.ToArray();

    public async Task<DeviceCommandWebSocketSession> RegisterAsync(
        Guid deviceId,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Ідентифікатор пристрою не може бути порожнім.", nameof(deviceId));
        }

        ArgumentNullException.ThrowIfNull(socket);

        DeviceCommandWebSocketSession session = new(deviceId, socket);
        DeviceCommandWebSocketSession? previous;
        lock (_registrationLock)
        {
            _sessions.TryGetValue(deviceId, out previous);
            _sessions[deviceId] = session;
        }

        if (previous is not null)
        {
            using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await previous.CloseAsync(WebSocketCloseStatus.EndpointUnavailable,
                    "Відкрито нове з'єднання пристрою.", closeTimeout.Token);
            }
            catch (OperationCanceledException)
            {
                previous.Abort();
            }
        }

        return session;
    }

    public void Unregister(DeviceCommandWebSocketSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ((ICollection<KeyValuePair<Guid, DeviceCommandWebSocketSession>>)_sessions)
            .Remove(new KeyValuePair<Guid, DeviceCommandWebSocketSession>(session.DeviceId, session));
    }

    public async Task<bool> SendAsync(
        Guid deviceId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(deviceId, out DeviceCommandWebSocketSession? session))
        {
            return false;
        }

        return await session.SendAsync(payload, cancellationToken);
    }
}

public sealed class DeviceCommandWebSocketSession(Guid deviceId, WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid DeviceId { get; } = deviceId;

    public void Abort() => socket.Abort();

    public async Task<bool> SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            return true;
        }
        catch (WebSocketException)
        {
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async Task CloseAsync(
        WebSocketCloseStatus status,
        string description,
        CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseOutputAsync(status, description, cancellationToken);
            }
        }
        catch (WebSocketException)
        {
            socket.Abort();
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
