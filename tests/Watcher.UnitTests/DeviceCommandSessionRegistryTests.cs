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
