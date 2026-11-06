using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Watcher.Api.IntegrationTests;

public sealed class AutomaticAlertDispatchNoStoreTests
{
    [Fact]
    public async Task Unauthorized_and_forbidden_status_responses_are_not_cacheable()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using HttpResponseMessage anonymousResponse = await client.GetAsync(
            "/api/v1/automatic-alert-dispatch/status");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        Assert.True(anonymousResponse.Headers.CacheControl?.NoStore);

        using HttpRequestMessage operatorRequest = new(HttpMethod.Get,
            "/api/v1/automatic-alert-dispatch/status");
        operatorRequest.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        operatorRequest.Headers.Add("X-Test-Role", "notificator");
        using HttpResponseMessage operatorResponse = await client.SendAsync(operatorRequest);

        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
        Assert.True(operatorResponse.Headers.CacheControl?.NoStore);
    }
}
