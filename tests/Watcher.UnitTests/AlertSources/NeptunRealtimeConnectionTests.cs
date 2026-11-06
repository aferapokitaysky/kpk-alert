using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class NeptunRealtimeConnectionTests
{
    [Fact]
    public async Task Receive_combines_fragments_and_accepts_payload_at_byte_limit()
    {
        var expectedPayload = Encoding.UTF8.GetBytes("Проба");
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebApplication app = await StartServerAsync(async (socket, cancellationToken) =>
        {
            accepted.TrySetResult();
            await socket.SendAsync(expectedPayload.AsMemory(0, 1), WebSocketMessageType.Text,
                endOfMessage: false, cancellationToken);
            await socket.SendAsync(expectedPayload.AsMemory(1), WebSocketMessageType.Text,
                endOfMessage: true, cancellationToken);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
        });
        Uri streamUri = GetStreamUri(app);
        INeptunRealtimeConnectionFactory factory = new NeptunRealtimeConnectionFactory();
        await using INeptunRealtimeConnection connection = await factory.ConnectAsync(
            streamUri, CancellationToken.None);

        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        NeptunRealtimeMessage message = await connection.ReceiveAsync(
            expectedPayload.Length, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Text, message.MessageType);
        Assert.Equal(expectedPayload, message.Payload);
    }

    [Fact]
    public async Task Receive_uses_utf8_byte_count_instead_of_character_count_for_limit()
    {
        var payload = Encoding.UTF8.GetBytes("Проба");
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebApplication app = await StartServerAsync(async (socket, cancellationToken) =>
        {
            accepted.TrySetResult();
            await socket.SendAsync(payload.AsMemory(0, 1), WebSocketMessageType.Text,
                endOfMessage: false, cancellationToken);
            await socket.SendAsync(payload.AsMemory(1), WebSocketMessageType.Text,
                endOfMessage: true, cancellationToken);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
        });
        INeptunRealtimeConnectionFactory factory = new NeptunRealtimeConnectionFactory();
        await using INeptunRealtimeConnection connection = await factory.ConnectAsync(
            GetStreamUri(app), CancellationToken.None);

        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await connection.ReceiveAsync(maximumMessageBytes: 5, CancellationToken.None));
    }

    [Fact]
    public async Task Receive_rejects_a_fragmented_message_that_exceeds_byte_limit()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebApplication app = await StartServerAsync(async (socket, cancellationToken) =>
        {
            accepted.TrySetResult();
            await socket.SendAsync(new byte[4].AsMemory(), WebSocketMessageType.Text,
                endOfMessage: false, cancellationToken);
            await socket.SendAsync(new byte[4].AsMemory(), WebSocketMessageType.Text,
                endOfMessage: true, cancellationToken);
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", cancellationToken);
        });
        INeptunRealtimeConnectionFactory factory = new NeptunRealtimeConnectionFactory();
        await using INeptunRealtimeConnection connection = await factory.ConnectAsync(
            GetStreamUri(app), CancellationToken.None);

        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await connection.ReceiveAsync(maximumMessageBytes: 7, CancellationToken.None));
    }

    [Fact]
    public async Task Receive_propagates_cancellation_while_waiting_for_a_frame()
    {
        var accepted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using WebApplication app = await StartServerAsync(async (_, cancellationToken) =>
        {
            accepted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        INeptunRealtimeConnectionFactory factory = new NeptunRealtimeConnectionFactory();
        await using INeptunRealtimeConnection connection = await factory.ConnectAsync(
            GetStreamUri(app), CancellationToken.None);
        using CancellationTokenSource cancellation = new();

        Task<NeptunRealtimeMessage> receive = connection.ReceiveAsync(16, cancellation.Token);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => receive);
    }

    private static async Task<WebApplication> StartServerAsync(
        Func<WebSocket, CancellationToken, Task> handleConnection)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Map("/stream", async context =>
        {
            using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync();
            await handleConnection(socket, context.RequestAborted);
        });
        await app.StartAsync();
        return app;
    }

    private static Uri GetStreamUri(WebApplication app)
    {
        IServer server = app.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        UriBuilder uri = new(address)
        {
            Scheme = "ws",
            Path = "/stream"
        };
        return uri.Uri;
    }
}
