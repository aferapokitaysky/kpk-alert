using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Watcher.Api.AlertSources;

namespace Watcher.Api.IntegrationTests;

public sealed class AlertSourcesEndpointTests
{
    [Fact]
    public async Task Status_is_public_and_uses_registered_alert_source_service()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IAlertSourceProvider>();
                services.AddSingleton<IAlertSourceProvider, FixedAlertSourceProvider>();
            }));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/alert-sources/status");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(response.Headers.CacheControl);
        Assert.True(response.Headers.CacheControl.NoStore);
        using var document = JsonDocument.Parse(content);
        JsonElement targets = document.RootElement.GetProperty("targets");
        JsonElement hromada = Assert.Single(targets.EnumerateArray(), target =>
            target.GetProperty("scope").GetString() == "hromada");
        Assert.Equal("unknown", hromada.GetProperty("level").GetString());
        Assert.Equal("fresh", hromada.GetProperty("freshness").GetString());
    }

    private sealed class FixedAlertSourceProvider : IAlertSourceProvider
    {
        public Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AlertSourceObservation>>(
            [
                new(
                    AlertSourceCatalog.AlertsInUaCode,
                    AlertSourceCatalog.AlertsInUaName,
                    AlertScope.Hromada,
                    AlertSourceCatalog.KremenchukHromadaId,
                    AlertSourceCatalog.KremenchukHromadaName,
                    AlertState.Unknown,
                    null,
                    AlertFreshness.Fresh,
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    AlertSourceCatalog.AlertsInUaAttributionUrl)
            ]);

        public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
            DateTimeOffset checkedAtUtc,
            string errorCode) =>
            [new(
                AlertSourceCatalog.AlertsInUaCode,
                AlertSourceCatalog.AlertsInUaName,
                AlertScope.Hromada,
                AlertSourceCatalog.KremenchukHromadaId,
                AlertSourceCatalog.KremenchukHromadaName,
                AlertState.Unknown,
                null,
                AlertFreshness.Unknown,
                checkedAtUtc,
                null,
                errorCode,
                AlertSourceCatalog.AlertsInUaAttributionUrl)];
    }
}
