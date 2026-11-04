using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Watcher.Api.IntegrationTests;

public sealed class AuthEndpointTests
{
    [Fact]
    public async Task Logout_requires_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync("/api/v1/auth/logout", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_expires_the_session_cookie_for_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());

        using HttpResponseMessage response = await client.SendAsync(request);
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = Assert.Single(cookies, value =>
            value.StartsWith("__Host-KgtaAlert.Session=", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
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

    private sealed class AuthTestWebApplicationFactory(
        bool googleEnabled,
        bool googleCredentialsConfigured) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            if (googleEnabled)
            {
                builder.ConfigureHostConfiguration(configuration =>
                {
                    Dictionary<string, string?> settings = new()
                    {
                        ["Authentication:Google:Enabled"] = "true"
                    };
                    if (googleCredentialsConfigured)
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
            });
        }
    }
}
