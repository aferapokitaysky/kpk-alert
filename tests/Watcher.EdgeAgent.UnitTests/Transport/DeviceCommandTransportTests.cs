using System.Net;
using System.Text;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Transport;

namespace Watcher.EdgeAgent.UnitTests.Transport;

public sealed class DeviceCommandTransportTests
{
    private static readonly Guid DeviceId = Guid.Parse("8f1d36d9-2c8f-4f12-a204-5135d227cd42");
    private static readonly Guid LocationId = Guid.Parse("ce401d2a-844c-4603-b20c-46f19fd141ee");

    [Fact]
    public async Task GetIdentityAsync_DeserializesValidIdentity()
    {
        using HttpClient client = CreateClient(HttpStatusCode.OK,
            $$"""{"deviceId":"{{DeviceId}}","locationId":"{{LocationId}}"}""");
        DeviceCommandTransport transport = new(CreateOptions(), null!);

        DeviceIdentity identity = await transport.GetIdentityAsync(client, CancellationToken.None);

        Assert.Equal(DeviceId, identity.DeviceId);
        Assert.Equal(LocationId, identity.LocationId);
    }

    [Fact]
    public async Task GetIdentityAsync_NonSuccessStatusHasStableDiagnostic()
    {
        using HttpClient client = CreateClient(HttpStatusCode.Unauthorized, "{}");
        DeviceCommandTransport transport = new(CreateOptions(), null!);

        EdgeAgentTransportException exception = await Assert.ThrowsAsync<EdgeAgentTransportException>(
            () => transport.GetIdentityAsync(client, CancellationToken.None));

        Assert.Equal("identity_request_failed", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"deviceId\":\"00000000-0000-0000-0000-000000000000\",\"locationId\":\"ce401d2a-844c-4603-b20c-46f19fd141ee\"}")]
    [InlineData("{\"deviceId\":\"8f1d36d9-2c8f-4f12-a204-5135d227cd42\",\"locationId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"deviceId\":\"8f1d36d9-2c8f-4f12-a204-5135d227cd42\",\"locationId\":\"ce401d2a-844c-4603-b20c-46f19fd141ee\",\"unexpected\":true}")]
    public async Task GetIdentityAsync_InvalidJsonOrIdentityIsRejected(string responseBody)
    {
        using HttpClient client = CreateClient(HttpStatusCode.OK, responseBody);
        DeviceCommandTransport transport = new(CreateOptions(), null!);

        EdgeAgentTransportException exception = await Assert.ThrowsAsync<EdgeAgentTransportException>(
            () => transport.GetIdentityAsync(client, CancellationToken.None));

        Assert.Equal("identity_response_invalid", exception.DiagnosticCode);
    }

    [Fact]
    public async Task GetIdentityAsync_OversizedResponseIsRejectedBeforeDeserialization()
    {
        using HttpClient client = CreateClient(HttpStatusCode.OK, new string('x', 8 * 1024 + 1));
        DeviceCommandTransport transport = new(CreateOptions(), null!);

        EdgeAgentTransportException exception = await Assert.ThrowsAsync<EdgeAgentTransportException>(
            () => transport.GetIdentityAsync(client, CancellationToken.None));

        Assert.Equal("identity_response_too_large", exception.DiagnosticCode);
    }

    [Fact]
    public async Task GetIdentityAsync_PropagatesCallerCancellation()
    {
        using HttpClient client = new(new DelegateHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token)));
        DeviceCommandTransport transport = new(CreateOptions(), null!);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => transport.GetIdentityAsync(client, cancellation.Token));
    }

    [Fact]
    public async Task RunAsync_RejectsInvalidConfigurationBeforeCertificateLookup()
    {
        EdgeAgentOptions options = CreateOptions(new Uri("http://watcher.example.test"));
        DeviceCommandTransport transport = new(options, null!);

        EdgeAgentTransportException exception = await Assert.ThrowsAsync<EdgeAgentTransportException>(
            () => transport.RunAsync(CancellationToken.None));

        Assert.Equal("invalid_configuration", exception.DiagnosticCode);
    }

    [Fact]
    public async Task RunAsync_RequiresConfiguredCertificateThumbprint()
    {
        DeviceCommandTransport transport = new(CreateOptions(), null!);

        EdgeAgentTransportException exception = await Assert.ThrowsAsync<EdgeAgentTransportException>(
            () => transport.RunAsync(CancellationToken.None));

        Assert.Equal("device_certificate_unavailable", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ReportProcessingOutcomeAsync_NotifiesQueuePressureWithoutClosingConnection()
    {
        string? reportedDiagnostic = null;
        DeviceCommandTransport transport = new(CreateOptions(), null!, (code, _) =>
        {
            reportedDiagnostic = code;
            return ValueTask.CompletedTask;
        });

        await transport.ReportProcessingOutcomeAsync(new(
            Accepted: false,
            Duplicate: false,
            CloseConnection: false,
            DiagnosticCode: "receipt_queue_full",
            State: null));

        Assert.Equal("receipt_queue_full", reportedDiagnostic);
    }

    private static HttpClient CreateClient(HttpStatusCode statusCode, string content) =>
        new(new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        })));

    private static EdgeAgentOptions CreateOptions(Uri? baseAddress = null) => new()
    {
        BaseAddress = baseAddress ?? new Uri("https://watcher.example.test"),
        IdentityPath = "/api/v1/device/identity",
        WebSocketPath = "/api/v1/device/commands/stream",
        ReceiptPath = "/api/v1/device/command-receipts",
        DatabasePath = "unused.db",
        AudioRootDirectory = "unused-audio"
    };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
