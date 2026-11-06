using System.Text.Json;
using System.Text.Json.Serialization;
using Watcher.EdgeAgent.Contracts;

namespace Watcher.EdgeAgent.UnitTests.Contracts;

public sealed class EdgeAgentContractSerializationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void StatusRoundTripsWithWebNamesStringEnumsAndOptionalValues()
    {
        EdgeAgentStatus status = new(
            SchemaVersion: 1,
            ObservedAtUtc: DateTimeOffset.Parse("2026-11-06T10:15:30+00:00"),
            ConnectionState: EdgeAgentConnectionState.Reconnecting,
            DeviceId: Guid.Parse("4bb6d9db-5bca-46c4-a1d9-07f20a84534f"),
            LocationId: null,
            AudioState: "available",
            AudioEndpointId: "endpoint-1",
            LastCommandId: null,
            LastSequence: null,
            LastCommandState: null,
            PendingReceiptCount: 2,
            DiagnosticCode: "network_retry");

        var json = JsonSerializer.Serialize(status, JsonOptions);
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Reconnecting", root.GetProperty("connectionState").GetString());
        Assert.Equal(status.DeviceId, root.GetProperty("deviceId").GetGuid());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("locationId").ValueKind);

        EdgeAgentStatus? roundTripped = JsonSerializer.Deserialize<EdgeAgentStatus>(json, JsonOptions);

        Assert.Equal(status, roundTripped);
    }

    [Fact]
    public void PipeRequestAndResponseUseStableOperationAndPayloadFields()
    {
        EdgeAgentPipeRequest request = new(1, "get_status");
        EdgeAgentPipeResponse response = new(1, true, null, null);

        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var responseJson = JsonSerializer.Serialize(response, JsonOptions);
        EdgeAgentPipeRequest? roundTrippedRequest = JsonSerializer.Deserialize<EdgeAgentPipeRequest>(requestJson, JsonOptions);
        EdgeAgentPipeResponse? roundTrippedResponse = JsonSerializer.Deserialize<EdgeAgentPipeResponse>(responseJson, JsonOptions);

        Assert.Equal(request, roundTrippedRequest);
        Assert.Equal(response, roundTrippedResponse);
        Assert.Contains("\"operation\":\"get_status\"", requestJson);
        Assert.Contains("\"succeeded\":true", responseJson);
        Assert.Contains("\"diagnosticCode\":null", responseJson);
    }
}
