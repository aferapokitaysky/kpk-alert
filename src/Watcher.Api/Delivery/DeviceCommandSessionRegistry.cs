using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Watcher.Api.Delivery;

public sealed class DeviceCommandSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, DeviceCommandWebSocketSession> _sessions = new();
    private readonly DeviceCommandSessionRegistrationGate _registrationGate = new();

    public IReadOnlyCollection<Guid> ConnectedDeviceIds => _sessions.Keys.ToArray();

    public IReadOnlyCollection<(Guid DeviceId, Guid PresenceSessionId)> ConnectedSessions =>
        _sessions.Values.Select(session => (session.DeviceId, session.PresenceSessionId)).ToArray();

    public async Task<DeviceCommandWebSocketSession> RegisterAsync(
        Guid deviceId,
        WebSocket socket,
        CancellationToken cancellationToken) =>
        await RegisterAsync(deviceId, Guid.Empty, socket, cancellationToken);

    public async Task<DeviceCommandWebSocketSession> RegisterAsync(
        Guid deviceId,
        Guid presenceSessionId,
        WebSocket socket,
        CancellationToken cancellationToken) =>
        await RegisterCoreAsync(deviceId, presenceSessionId, socket, null, cancellationToken)
            ?? throw new InvalidOperationException("Не вдалося зареєструвати WebSocket-сеанс пристрою.");

    public Task<DeviceCommandWebSocketSession?> TryRegisterIfCurrentAsync(
        Guid deviceId,
        Guid presenceSessionId,
        WebSocket socket,
        Func<CancellationToken, Task<bool>> isCurrentSessionAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isCurrentSessionAsync);
        return RegisterCoreAsync(deviceId, presenceSessionId, socket, isCurrentSessionAsync,
            cancellationToken);
    }

    private async Task<DeviceCommandWebSocketSession?> RegisterCoreAsync(
        Guid deviceId,
        Guid presenceSessionId,
        WebSocket socket,
        Func<CancellationToken, Task<bool>>? isCurrentSessionAsync,
        CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Ідентифікатор пристрою не може бути порожнім.", nameof(deviceId));
        }

        ArgumentNullException.ThrowIfNull(socket);

        DeviceCommandWebSocketSession session = new(deviceId, presenceSessionId, socket);
        DeviceCommandWebSocketSession? previous;
        using (await _registrationGate.EnterAsync(deviceId, cancellationToken))
        {
            if (isCurrentSessionAsync is not null && !await isCurrentSessionAsync(cancellationToken))
            {
                return null;
            }

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

    public async Task DisconnectAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Ідентифікатор пристрою не може бути порожнім.", nameof(deviceId));
        }

        DeviceCommandWebSocketSession? session;
        using (await _registrationGate.EnterAsync(deviceId, cancellationToken))
        {
            _sessions.TryRemove(deviceId, out session);
        }

        if (session is null)
        {
            return;
        }

        using var closeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        closeTimeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await session.CloseAsync(WebSocketCloseStatus.PolicyViolation,
                "Сертифікат пристрою відкликано.", closeTimeout.Token);
        }
        catch (OperationCanceledException)
        {
            session.Abort();
        }
    }

    public void Abort(Guid deviceId, Guid presenceSessionId)
    {
        if (_sessions.TryGetValue(deviceId, out DeviceCommandWebSocketSession? session) &&
            session.PresenceSessionId == presenceSessionId)
        {
            session.Abort();
        }
    }

    public async Task<DeviceCommandSendOutcome> SendAsync(
        Guid deviceId,
        Guid presenceSessionId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(deviceId, out DeviceCommandWebSocketSession? session) ||
            session.PresenceSessionId != presenceSessionId)
        {
            return DeviceCommandSendOutcome.NotStarted;
        }

        return await session.SendAsync(payload, cancellationToken);
    }

    public Task<DeviceCommandSendOutcome> SendAsync(
        Guid deviceId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken) =>
        _sessions.TryGetValue(deviceId, out DeviceCommandWebSocketSession? session)
            ? SendAsync(deviceId, session.PresenceSessionId, payload, cancellationToken)
            : Task.FromResult(DeviceCommandSendOutcome.NotStarted);
}

public enum DeviceCommandSendOutcome
{
    Completed,
    NotStarted,
    Indeterminate
}

public sealed class DeviceCommandWebSocketSession(Guid deviceId, Guid presenceSessionId, WebSocket socket)
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public Guid DeviceId { get; } = deviceId;
    public Guid PresenceSessionId { get; } = presenceSessionId;

    public void Abort() => socket.Abort();

    public async Task<DeviceCommandSendOutcome> SendAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        try
        {
            await _sendLock.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return DeviceCommandSendOutcome.NotStarted;
        }

        try
        {
            WebSocketState state;
            try
            {
                state = socket.State;
            }
            catch (ObjectDisposedException)
            {
                return DeviceCommandSendOutcome.NotStarted;
            }

            if (state != WebSocketState.Open)
            {
                return DeviceCommandSendOutcome.NotStarted;
            }

            await socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
            return DeviceCommandSendOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            socket.Abort();
            throw;
        }
        catch (Exception exception) when (exception is WebSocketException or ObjectDisposedException)
        {
            socket.Abort();
            return DeviceCommandSendOutcome.Indeterminate;
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
