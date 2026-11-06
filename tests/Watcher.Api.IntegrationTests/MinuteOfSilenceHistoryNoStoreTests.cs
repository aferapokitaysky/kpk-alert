using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Watcher.Api.IntegrationTests;

public sealed class MinuteOfSilenceHistoryNoStoreTests
{
    [Fact]
    public async Task Unauthorized_history_response_is_not_cacheable()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage response = await client.GetAsync(
            "/api/v1/minute-of-silence/history?from=2026-10-01&to=2026-10-02");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task Invalid_history_query_binding_response_is_not_cacheable()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });
        using HttpRequestMessage request = new(HttpMethod.Get,
            "/api/v1/minute-of-silence/history?from=not-a-date&to=2026-10-02");
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-Test-Role", "admin");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    private static WebApplicationFactory<Program> CreateFactory() => new AuthTestWebApplicationFactory(
        googleEnabled: false, useInMemoryDatabase: true);
}
