using System.Net.WebSockets;
using Watcher.Api.Delivery;

namespace Watcher.UnitTests;

public sealed class DeviceCommandSessionRegistryTests
{
    [Fact]
    public async Task Replacing_session_closes_previous_and_old_disconnect_cannot_remove_current()
    {
        DeviceCommandSessionRegistry registry = new();
        var deviceId = Guid.NewGuid();
        FakeWebSocket firstSocket = new();
        FakeWebSocket secondSocket = new();

        DeviceCommandWebSocketSession first = await registry.RegisterAsync(
            deviceId, firstSocket, CancellationToken.None);
        DeviceCommandWebSocketSession second = await registry.RegisterAsync(
            deviceId, secondSocket, CancellationToken.None);

        Assert.Equal(WebSocketState.CloseSent, firstSocket.State);
        Assert.Equal([deviceId], registry.ConnectedDeviceIds);
        registry.Unregister(first);

        Assert.True(await registry.SendAsync(deviceId, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(1, secondSocket.SendCount);

        registry.Unregister(second);
        Assert.Empty(registry.ConnectedDeviceIds);
    }

    [Fact]
    public async Task Register_rejects_empty_device_id()
    {
        DeviceCommandSessionRegistry registry = new();

        await Assert.ThrowsAsync<ArgumentException>(() => registry.RegisterAsync(
            Guid.Empty, new FakeWebSocket(), CancellationToken.None));
    }

    [Fact]
    public async Task Sending_to_disconnected_device_returns_false()
    {
        DeviceCommandSessionRegistry registry = new();

        Assert.False(await registry.SendAsync(
            Guid.NewGuid(), "{}"u8.ToArray(), CancellationToken.None));
    }

    [Fact]
    public async Task Delayed_stale_handshake_does_not_replace_or_close_current_session()
    {
        DeviceCommandSessionRegistry registry = new();
        var deviceId = Guid.NewGuid();
        var currentPresenceSessionId = Guid.NewGuid();
        FakeWebSocket currentSocket = new();
        DeviceCommandWebSocketSession currentSession = await registry.RegisterAsync(
            deviceId, currentPresenceSessionId, currentSocket, CancellationToken.None);

        FakeWebSocket staleSocket = new();
        DeviceCommandWebSocketSession? staleSession = await registry.TryRegisterIfCurrentAsync(
            deviceId,
            Guid.NewGuid(),
            staleSocket,
            _ => Task.FromResult(false),
            CancellationToken.None);

        Assert.Null(staleSession);
        Assert.Equal(WebSocketState.Open, currentSocket.State);
        Assert.Equal(WebSocketState.Open, staleSocket.State);
        Assert.True(await registry.SendAsync(
            deviceId, currentPresenceSessionId, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(1, currentSocket.SendCount);
        registry.Unregister(currentSession);
    }

    [Fact]
    public async Task Concurrent_handshakes_register_in_validation_order_for_same_device()
    {
        DeviceCommandSessionRegistry registry = new();
        var deviceId = Guid.NewGuid();
        var olderPresenceSessionId = Guid.NewGuid();
        var newerPresenceSessionId = Guid.NewGuid();
        var allowOlderValidation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var olderValidationStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        FakeWebSocket olderSocket = new();
        Task<DeviceCommandWebSocketSession?> olderRegistration = registry.TryRegisterIfCurrentAsync(
            deviceId,
            olderPresenceSessionId,
            olderSocket,
            async cancellationToken =>
            {
                olderValidationStarted.SetResult();
                await allowOlderValidation.Task.WaitAsync(cancellationToken);
                return true;
            },
            CancellationToken.None);

        await olderValidationStarted.Task;
        FakeWebSocket newerSocket = new();
        Task<DeviceCommandWebSocketSession?> newerRegistration = registry.TryRegisterIfCurrentAsync(
            deviceId,
            newerPresenceSessionId,
            newerSocket,
            _ => Task.FromResult(true),
            CancellationToken.None);

        allowOlderValidation.SetResult();
        DeviceCommandWebSocketSession? olderSession = await olderRegistration;
        DeviceCommandWebSocketSession? newerSession = await newerRegistration;

        Assert.NotNull(olderSession);
        Assert.NotNull(newerSession);
        Assert.Equal(WebSocketState.CloseSent, olderSocket.State);
        Assert.True(await registry.SendAsync(
            deviceId, newerPresenceSessionId, "{}"u8.ToArray(), CancellationToken.None));
        Assert.Equal(1, newerSocket.SendCount);
        Assert.False(await registry.SendAsync(
            deviceId, olderPresenceSessionId, "{}"u8.ToArray(), CancellationToken.None));
        registry.Unregister(newerSession);
    }

    private sealed class FakeWebSocket : WebSocket
    {
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public int SendCount { get; private set; }
        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => CloseOutputAsync(
            closeStatus, statusDescription, cancellationToken);

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken) =>
            Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }
}
