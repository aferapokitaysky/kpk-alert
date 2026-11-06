using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.UnitTests.Transport;

public sealed class CommandReceiptSenderTests
{
    [Fact]
    public async Task SendOnceAsync_AcceptedResponseMarksReceiptDelivered()
    {
        using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Accepted)));
        CommandReceiptSender sender = CreateSender(client);

        ReceiptSendResult result = await sender.SendOnceAsync(CreateReceipt(), CancellationToken.None);

        Assert.Equal(ReceiptSendDisposition.Delivered, result.Disposition);
    }

    [Fact]
    public async Task SendOnceAsync_OkWithoutDuplicateMarkerIsPermanentFailure()
    {
        using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { duplicate = false })
        }));
        CommandReceiptSender sender = CreateSender(client);

        ReceiptSendResult result = await sender.SendOnceAsync(CreateReceipt(), CancellationToken.None);

        Assert.Equal(ReceiptSendDisposition.PermanentFailure, result.Disposition);
    }

    [Fact]
    public async Task SendOnceAsync_DuplicateResponseMarksReceiptDelivered()
    {
        using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { duplicate = true })
        }));
        CommandReceiptSender sender = CreateSender(client);

        ReceiptSendResult result = await sender.SendOnceAsync(CreateReceipt(), CancellationToken.None);

        Assert.Equal(ReceiptSendDisposition.Delivered, result.Disposition);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task SendOnceAsync_TransientStatusSchedulesRetry(HttpStatusCode status)
    {
        using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(status)));
        CommandReceiptSender sender = CreateSender(client);

        ReceiptSendResult result = await sender.SendOnceAsync(CreateReceipt(), CancellationToken.None);

        Assert.Equal(ReceiptSendDisposition.Retry, result.Disposition);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task SendOnceAsync_PermanentStatusIsNotRetried(HttpStatusCode status)
    {
        using HttpClient client = new(new StubHandler(_ => new HttpResponseMessage(status)));
        CommandReceiptSender sender = CreateSender(client);

        ReceiptSendResult result = await sender.SendOnceAsync(CreateReceipt(), CancellationToken.None);

        Assert.Equal(ReceiptSendDisposition.PermanentFailure, result.Disposition);
    }

    [Fact]
    public async Task SendOnceAsync_PreservesRetryAfter()
    {
        using HttpClient client = new(new StubHandler(_ =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(17));
            return response;
        }));
        CommandReceiptSender sender = CreateSender(client);

        ReceiptSendResult result = await sender.SendOnceAsync(CreateReceipt(), CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter);
    }

    [Fact]
    public async Task SendOnceAsync_UsesTheDurableReceiptFieldsWithoutChangingEventId()
    {
        PendingCommandReceipt receipt = CreateReceipt();
        JsonDocument? sent = null;
        using HttpClient client = new(new StubHandler(async request =>
        {
            sent = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }));
        CommandReceiptSender sender = CreateSender(client);

        await sender.SendOnceAsync(receipt, CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Equal(receipt.EventId, sent.RootElement.GetProperty("eventId").GetGuid());
        Assert.Equal(receipt.CommandId, sent.RootElement.GetProperty("commandId").GetGuid());
        Assert.Equal(receipt.Sequence, sent.RootElement.GetProperty("sequence").GetInt64());
        Assert.Equal("playing", sent.RootElement.GetProperty("state").GetString());
        Assert.Equal(receipt.OccurredAtUtc, sent.RootElement.GetProperty("occurredAtUtc").GetDateTimeOffset());
    }

    private static CommandReceiptSender CreateSender(HttpClient client)
    {
        client.BaseAddress = new Uri("https://watcher.example.test");
        return new CommandReceiptSender(
        new EdgeAgentOptions
        {
            BaseAddress = new Uri("https://watcher.example.test"),
            IdentityPath = "/api/v1/device/identity",
            WebSocketPath = "/api/v1/device/commands/stream",
            ReceiptPath = "/api/v1/device/command-receipts",
            DatabasePath = "unused.db",
            AudioRootDirectory = "unused-audio"
        },
        new StubStore(),
        client,
        new FixedClock());
    }

    private static PendingCommandReceipt CreateReceipt() => new(
        Guid.NewGuid(), Guid.NewGuid(), 1,
        Watcher.Contracts.DeviceCommands.DeviceCommandState.Playing,
        DateTimeOffset.Parse("2026-11-05T18:00:00Z"), null, 0,
        DateTimeOffset.Parse("2026-11-05T18:00:00Z"));

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));

        public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
            : this(request => handler(request).GetAwaiter().GetResult()) { }
    }

    private sealed class StubStore : IEdgeAgentStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<IReadOnlyList<PendingCommandReceipt>> RecoverInterruptedPlaybackAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<ProcessedCommand?> FindCommandAsync(Guid commandId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task RequeueLatestReceiptAsync(Guid commandId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<AcceptCommandResult> TryAcceptAsync(Watcher.Contracts.DeviceCommands.CommandDispatch command, string payloadHash, DateTimeOffset nowUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<bool> RecordRejectedCommandAsync(Watcher.Contracts.DeviceCommands.CommandDispatch command, string payloadHash, Watcher.Contracts.DeviceCommands.DeviceCommandState state, string diagnosticCode, DateTimeOffset nowUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PendingCommandReceipt> AddReceiptAsync(Guid commandId, long sequence, Watcher.Contracts.DeviceCommands.DeviceCommandState state, DateTimeOffset occurredAtUtc, string? diagnosticCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task UpdateCommandStateAsync(Guid commandId, Watcher.Contracts.DeviceCommands.DeviceCommandState state, string? diagnosticCode, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task TransitionCommandAsync(Guid commandId, Watcher.Contracts.DeviceCommands.DeviceCommandState state, string? diagnosticCode, DateTimeOffset occurredAtUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<int> GetPendingReceiptCountAsync(CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task<PendingCommandReceipt?> GetNextReceiptAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task MarkReceiptDeliveredAsync(Guid eventId, CancellationToken cancellationToken) => throw new NotImplementedException();
        public Task ScheduleReceiptRetryAsync(Guid eventId, DateTimeOffset nextAttemptAtUtc, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    private sealed class FixedClock : IEdgeAgentClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public long GetTimestamp() => 0;
        public TimeSpan GetElapsedTime(long startingTimestamp) => TimeSpan.Zero;
    }
}
