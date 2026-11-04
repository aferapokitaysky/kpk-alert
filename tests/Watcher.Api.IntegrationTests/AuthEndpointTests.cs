using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class AuthEndpointTests
{
    [Fact]
    public async Task Logout_requires_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateSecureClient(factory);
        var csrfToken = await GetCsrfTokenAsync(client);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_expires_the_session_cookie_for_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);
        var userId = Guid.NewGuid();
        var csrfToken = await GetCsrfTokenAsync(client, userId);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Очікувався 204, отримано {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = Assert.Single(cookies, value =>
            value.StartsWith("__Host-KgtaAlert.Session=", StringComparison.Ordinal));

        Assert.Contains("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        AuditEvent audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("auth.logout", audit.EventType);
        Assert.Equal(userId, audit.ActorId);
        Assert.Contains("session", audit.Payload);
        Assert.DoesNotContain("token", audit.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Csrf_token_is_returned_with_a_secure_host_only_cookie()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/csrf-token");
        CsrfTokenResponse? token = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("__Host-KgtaAlert.AntiForgery=", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(token?.Token));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Alert_command_rejects_an_authenticated_request_without_a_csrf_token()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/alert-commands")
        {
            Content = JsonContent.Create(new
            {
                action = "start",
                presetCode = "evacuation",
                locationIds = new[] { Guid.NewGuid() }
            })
        };
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Очікувався 400, отримано {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Login_starts_google_oauth_when_credentials_are_configured()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleEnabled: true);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/login?returnUrl=%2Fdashboard");

        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("accounts.google.com", response.Headers.Location!.Host);
        Assert.Contains("test-client-id", response.Headers.Location.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_fails_when_google_login_is_enabled_without_credentials()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: true, googleCredentialsConfigured: false);

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    private static WebApplicationFactory<Program> CreateFactory(bool googleEnabled = false) =>
        new AuthTestWebApplicationFactory(googleEnabled, googleCredentialsConfigured: true);

    private static HttpClient CreateSecureClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<string> GetCsrfTokenAsync(HttpClient client, Guid? userId = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        if (userId is Guid id)
        {
            request.Headers.Add("X-Test-User-Id", id.ToString());
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        CsrfTokenResponse? result = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        return result?.Token ?? throw new InvalidOperationException("API не повернув antiforgery token.");
    }

    private sealed record CsrfTokenResponse(string Token);
}

internal sealed class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly bool _googleEnabled;
    private readonly bool _googleCredentialsConfigured;
    private readonly bool _useInMemoryDatabase;
    private readonly string _inMemoryDatabaseName = Guid.NewGuid().ToString();

    public AuthTestWebApplicationFactory(
        bool googleEnabled,
        bool googleCredentialsConfigured = true,
        bool useInMemoryDatabase = false)
    {
        _googleEnabled = googleEnabled;
        _googleCredentialsConfigured = googleCredentialsConfigured;
        _useInMemoryDatabase = useInMemoryDatabase;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_googleEnabled)
        {
            builder.ConfigureHostConfiguration(configuration =>
            {
                Dictionary<string, string?> settings = new()
                {
                    ["Authentication:Google:Enabled"] = "true"
                };
                if (_googleCredentialsConfigured)
                {
                    settings["Authentication:Google:ClientId"] = "test-client-id";
                    settings["Authentication:Google:ClientSecret"] = "test-client-secret";
                }

                configuration.AddInMemoryCollection(settings);
            });
        }

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName, _ => { });

            if (_googleEnabled || _useInMemoryDatabase)
            {
                services.RemoveAll<WatcherDbContext>();
                services.RemoveAll<DbContextOptions<WatcherDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<WatcherDbContext>>();
                services.AddDbContext<WatcherDbContext>(options =>
                    options.UseInMemoryDatabase(_inMemoryDatabaseName));
            }
        });
    }
}
