using Watcher.EdgeAgent.Infrastructure.Audio;

namespace Watcher.EdgeAgent.UnitTests.Audio;

public sealed class WasapiAudioEndpointAvailabilityMonitorTests
{
    [Fact]
    public void ActiveConfiguredEndpointIsAvailable()
    {
        string? probedEndpointId = null;

        var available = WasapiAudioEndpointAvailabilityMonitor.IsEndpointActive("stable-endpoint-id", id =>
        {
            probedEndpointId = id;
            return true;
        });

        Assert.True(available);
        Assert.Equal("stable-endpoint-id", probedEndpointId);
    }

    [Fact]
    public void MissingConfigurationIsUnavailableWithoutProbingDefaultEndpoint()
    {
        var probed = false;

        var available = WasapiAudioEndpointAvailabilityMonitor.IsEndpointActive(null, _ =>
        {
            probed = true;
            return true;
        });

        Assert.False(available);
        Assert.False(probed);
    }

    [Fact]
    public void InactiveEndpointIsUnavailable()
    {
        Assert.False(WasapiAudioEndpointAvailabilityMonitor.IsEndpointActive("stable-endpoint-id", _ => false));
    }

    [Fact]
    public void ProbeFailureIsUnavailable()
    {
        Assert.False(WasapiAudioEndpointAvailabilityMonitor.IsEndpointActive("stable-endpoint-id",
            _ => throw new InvalidOperationException("probe failed")));
    }
}
