using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class NeptunProviderRaceTests
{
    [Fact]
    public async Task RestResponseStartedBeforeRealtimeModeCannotOverwriteRealtimeSnapshot()
    {
        TaskCompletionSource requestStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseResponse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeHttpClientFactory httpFactory = new(async (_, _, cancellationToken) =>
        {
            requestStarted.TrySetResult();
            await releaseResponse.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"oblasts":[],"raions":[]}
                    """, Encoding.UTF8, "application/json")
            };
        });
        NeptunProvider provider = new(httpFactory, TimeProvider.System, NullLogger<NeptunProvider>.Instance);

        Task<IReadOnlyList<AlertSourceObservation>> pendingRestRead = provider.ReadAsync(CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        provider.EnableRealtime();
        provider.ApplyRealtimeSnapshot(new(AlertState.Active, AlertState.Active));
        releaseResponse.TrySetResult();

        IReadOnlyList<AlertSourceObservation> result = await pendingRestRead;

        Assert.All(result, observation => Assert.Equal(AlertState.Active, observation.State));
        Assert.All(result, observation => Assert.Equal(AlertFreshness.Fresh, observation.Freshness));
    }
}
