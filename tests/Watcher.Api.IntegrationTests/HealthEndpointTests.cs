using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Watcher.Api.IntegrationTests;

public sealed class HealthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public HealthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
    }

    [Fact]
    public async Task Health_endpoint_returns_json_success()
    {
        using HttpResponseMessage response = await _client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_is_served_as_ukrainian_html()
    {
        using HttpResponseMessage response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("lang=\"uk\"", html);
        Assert.Contains("Пульт керування", html);
    }

    [Fact]
    public async Task Alert_command_requires_an_authenticated_user()
    {
        using HttpResponseMessage tokenResponse = await _client.GetAsync("/api/v1/auth/csrf-token");
        using var tokenBody = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync());
        var csrfToken = tokenBody.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("API не повернув antiforgery token.");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/alert-commands")
        {
            Content = JsonContent.Create(new
            {
                action = "start",
                presetCode = "evacuation",
                locationIds = new[] { Guid.NewGuid() }
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Google_login_reports_missing_configuration_without_secrets()
    {
        using HttpResponseMessage response = await _client.GetAsync("/api/v1/auth/login");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
