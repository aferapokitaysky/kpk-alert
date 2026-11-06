using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class AlertProviderIsolationTests
{
    [Fact]
    public async Task AlertsInUa_refreshes_healthy_scope_while_preserving_failed_scope_as_stale_context()
    {
        ConcurrentDictionary<string, int> callsByPath = new(StringComparer.Ordinal);
        IHttpClientFactory clientFactory = new StubHttpClientFactory((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var callNumber = callsByPath.AddOrUpdate(path, 1, (_, count) => count + 1);
            if (path.EndsWith("/1088.json", StringComparison.Ordinal) && callNumber > 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
            }

            var status = path.EndsWith("/19.json", StringComparison.Ordinal) && callNumber > 1
                ? "N"
                : "R";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"\"{status}\"", Encoding.UTF8, "application/json")
            });
        });
        AlertsInUaProvider provider = new(
            clientFactory,
            Options.Create(new AlertsInUaOptions("server-token")),
            TimeProvider.System,
            NullLogger<AlertsInUaProvider>.Instance);

        IReadOnlyList<AlertSourceObservation> initial = await provider.ReadAsync(CancellationToken.None);
        IReadOnlyList<AlertSourceObservation> refreshed = await provider.ReadAsync(CancellationToken.None);

        Assert.All(initial, observation => Assert.Equal(AlertState.Active, observation.State));
        AlertSourceObservation oblast = Assert.Single(refreshed, item => item.Scope == AlertScope.Oblast);
        Assert.Equal(AlertState.Unknown, oblast.State);
        Assert.Equal(AlertFreshness.Fresh, oblast.Freshness);
        Assert.Null(oblast.ErrorCode);
        Assert.Null(oblast.LastKnownState);

        AlertSourceObservation hromada = Assert.Single(refreshed, item => item.Scope == AlertScope.Hromada);
        Assert.Equal(AlertState.Unknown, hromada.State);
        Assert.Equal(AlertFreshness.Stale, hromada.Freshness);
        Assert.Equal("rate_limited", hromada.ErrorCode);
        Assert.Equal(AlertState.Active, hromada.LastKnownState);
        Assert.Equal("red", hromada.LastKnownAlertLevel);
    }

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(send))
        {
            BaseAddress = new Uri(AlertSourceCatalog.AlertsInUaBaseUrl)
        };
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
