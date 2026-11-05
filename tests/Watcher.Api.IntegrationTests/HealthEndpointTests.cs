using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpsPolicy;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Watcher.Api.Security;

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
    public async Task Liveness_is_successful_when_database_configuration_is_missing()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Watcher"] = ""
                })));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage live = await client.GetAsync("/api/v1/health/live");
        using HttpResponseMessage ready = await client.GetAsync("/api/v1/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("{\"status\":\"unavailable\",\"dependency\":\"database\"}",
            await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Dashboard_is_served_as_ukrainian_html()
    {
        using HttpResponseMessage response = await _client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ContentSecurityPolicyExtensions.Policy,
            response.Headers.GetValues("Content-Security-Policy").Single());
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("lang=\"uk\"", html);
        Assert.Contains("Пульт керування", html);
    }

    [Fact]
    public async Task Content_security_policy_is_fixed_for_csrf_api_and_ignores_forwarded_host()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/csrf-token");
        request.Headers.Add("X-Forwarded-Host", "attacker.example");

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var policy = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Equal(ContentSecurityPolicyExtensions.Policy, policy);
        Assert.DoesNotContain("attacker.example", policy);
        Assert.Contains("script-src 'self'", policy);
        Assert.Contains("style-src 'self'", policy);
        Assert.Contains("img-src 'self' data:", policy);
        Assert.DoesNotContain("'unsafe-inline'", policy);
        Assert.DoesNotContain("'unsafe-eval'", policy);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("token").GetString()));
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

    [Fact]
    public async Task Http_redirect_uses_the_public_https_port_configured_for_the_reverse_proxy()
    {
        using WebApplicationFactory<Program> factory = CreateReverseProxyFactory();
        Assert.Equal(443, factory.Services.GetRequiredService<IOptions<HttpsRedirectionOptions>>().Value.HttpsPort);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/api/v1/health");

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(new Uri("https://localhost:443/api/v1/health"), response.Headers.Location);
    }

    private static WebApplicationFactory<Program> CreateReverseProxyFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.Configure<HttpsRedirectionOptions>(options => options.HttpsPort = 443)));
}

public sealed class PostgresHealthEndpointTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PostgresHealthEndpointTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Readiness_is_successful_when_postgresql_is_available()
    {
        using HttpResponseMessage response = await _fixture.Client.GetAsync("/api/v1/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"status\":\"ok\"}", await response.Content.ReadAsStringAsync());
    }
}
