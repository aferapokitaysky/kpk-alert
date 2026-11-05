using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class UkraineAlarmApiClientTests
{
    [Fact]
    public async Task Uses_raw_authorization_header_and_reads_regions_and_action_index()
    {
        List<RequestSnapshot> requests = [];
        Queue<HttpResponseMessage> responses = new(
        [
            JsonResponse("""{"states":[{"regionId":"oblast-x","regionName":"Полтавська область","regionType":"State","regionChildIds":[{"regionId":"community-y","regionName":"Кременчуцька громада","regionType":"Community","regionChildIds":[]}]}]}"""),
            JsonResponse("""{"lastActionIndex":42}""")
        ]);
        UkraineAlarmApiClient client = CreateClient(async (request, _) =>
        {
            requests.Add(await CaptureAsync(request));
            return responses.Dequeue();
        });

        IReadOnlyList<UkraineAlarmRegion> regions = await client.GetRegionsAsync(CancellationToken.None);
        var actionIndex = await client.GetLastActionIndexAsync(CancellationToken.None);

        Assert.Equal(42, actionIndex);
        UkraineAlarmRegion state = Assert.Single(regions);
        Assert.Equal("oblast-x", state.RegionId);
        Assert.Equal("community-y", Assert.Single(state.Children).RegionId);
        Assert.Equal("/api/v3/regions", requests[0].Path);
        Assert.Equal("/api/v3/alerts/status", requests[1].Path);
        Assert.All(requests, request =>
        {
            Assert.Equal("test-token", request.Authorization);
            Assert.DoesNotContain("test-token", request.Path);
        });
    }

    [Fact]
    public async Task Registers_and_updates_https_webhook_with_documented_model()
    {
        List<RequestSnapshot> requests = [];
        UkraineAlarmApiClient client = CreateClient(async (request, _) =>
        {
            requests.Add(await CaptureAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        Uri callback = new("https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook");

        await client.RegisterWebhookAsync(callback, CancellationToken.None);
        await client.UpdateWebhookAsync(callback, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(HttpMethod.Patch, requests[1].Method);
        foreach (RequestSnapshot request in requests)
        {
            Assert.Equal("/api/v3/webhook", request.Path);
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal(callback.ToString(), body.RootElement.GetProperty("webHookUrl").GetString());
            Assert.Equal("test-token", request.Authorization);
        }
    }

    [Fact]
    public async Task Reads_alert_region_and_event_fields_from_region_endpoint()
    {
        UkraineAlarmApiClient client = CreateClient((_, _) => Task.FromResult(JsonResponse(
            """[{"regionId":"community-y","regionType":"Community","regionName":"Кременчуцька громада","activeAlerts":[{"regionId":"community-y","regionType":"Community","type":"Air"}]}]""")));

        IReadOnlyList<UkraineAlarmAlertRegion> regions = await client.GetRegionAlertsAsync(
            "community-y", CancellationToken.None);

        UkraineAlarmAlertRegion region = Assert.Single(regions);
        Assert.Equal("community-y", region.RegionId);
        Assert.Equal("Community", region.RegionType);
        UkraineAlarmAlert alert = Assert.Single(region.ActiveAlerts);
        Assert.Equal("community-y", alert.RegionId);
        Assert.Equal("Air", alert.Type);
    }

    [Fact]
    public async Task Rejects_non_https_callback_url_before_sending_request()
    {
        var requests = 0;
        UkraineAlarmApiClient client = CreateClient((_, _) =>
        {
            requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.RegisterWebhookAsync(new Uri("http://alerts.example.test/callback"), CancellationToken.None));

        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task Rejects_invalid_action_index_without_exposing_response_body()
    {
        UkraineAlarmApiClient client = CreateClient((_, _) =>
            Task.FromResult(JsonResponse("""{"lastActionIndex":"secret diagnostic"}""")));

        UkraineAlarmApiException exception = await Assert.ThrowsAsync<UkraineAlarmApiException>(
            () => client.GetLastActionIndexAsync(CancellationToken.None));

        Assert.Equal("invalid_response", exception.ErrorCode);
        Assert.DoesNotContain("secret diagnostic", exception.Message);
    }

    [Fact]
    public async Task Rejects_oversized_provider_response_before_parsing_it()
    {
        string oversizedBody = new('x', 4 * 1024 * 1024 + 1);
        UkraineAlarmApiClient client = CreateClient((_, _) => Task.FromResult(JsonResponse(oversizedBody)));

        UkraineAlarmApiException exception = await Assert.ThrowsAsync<UkraineAlarmApiException>(
            () => client.GetLastActionIndexAsync(CancellationToken.None));

        Assert.Equal("response_too_large", exception.ErrorCode);
        Assert.DoesNotContain(oversizedBody, exception.Message);
    }

    [Fact]
    public void Rejects_nonofficial_api_origin_before_adding_authorization_header()
    {
        HttpClient httpClient = new(new TestHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))))
        {
            BaseAddress = new Uri("https://attacker.example.test/")
        };

        Assert.Throws<InvalidOperationException>(() => new UkraineAlarmApiClient(
            httpClient,
            Options.Create(new UkraineAlarmOptions { Token = "test-token" })));
    }

    [Fact]
    public async Task Registration_service_uses_configured_https_origin_for_post_and_patch()
    {
        List<RequestSnapshot> requests = [];
        HttpClient httpClient = new(new TestHandler(async (request, _) =>
        {
            requests.Add(await CaptureAsync(request));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        UkraineAlarmOptions settings = new()
        {
            Token = "test-token",
            PublicBaseUrl = "https://alerts.example.test"
        };
        UkraineAlarmApiClient client = new(httpClient, Options.Create(settings));
        UkraineAlarmWebhookRegistrationService service = new(client, Options.Create(settings));

        await service.RegisterAsync(CancellationToken.None);
        await service.UpdateAsync(CancellationToken.None);

        Assert.Equal(HttpMethod.Post, requests[0].Method);
        Assert.Equal(HttpMethod.Patch, requests[1].Method);
        foreach (RequestSnapshot request in requests)
        {
            using var body = JsonDocument.Parse(request.Body!);
            Assert.Equal("https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook",
                body.RootElement.GetProperty("webHookUrl").GetString());
            Assert.DoesNotContain("token", request.Body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Registration_service_fails_closed_when_public_origin_is_missing()
    {
        var requestCount = 0;
        HttpClient httpClient = new(new TestHandler((_, _) =>
        {
            requestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }));
        UkraineAlarmOptions settings = new() { Token = "test-token" };
        UkraineAlarmWebhookRegistrationService service = new(
            new UkraineAlarmApiClient(httpClient, Options.Create(settings)),
            Options.Create(settings));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RegisterAsync(CancellationToken.None));

        Assert.Equal(0, requestCount);
    }

    private static UkraineAlarmApiClient CreateClient(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        HttpClient httpClient = new(new TestHandler(send));
        return new UkraineAlarmApiClient(httpClient, Options.Create(new UkraineAlarmOptions
        {
            Token = "test-token"
        }));
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static async Task<RequestSnapshot> CaptureAsync(HttpRequestMessage request) =>
        new(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.Headers.GetValues("Authorization").Single(),
            request.Content is null ? null : await request.Content.ReadAsStringAsync());

    private sealed record RequestSnapshot(HttpMethod Method, string Path, string Authorization, string? Body);

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
