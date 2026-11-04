using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            }));
}
