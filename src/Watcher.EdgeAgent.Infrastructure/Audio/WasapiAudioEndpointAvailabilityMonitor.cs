using NAudio.CoreAudioApi;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Audio;

public sealed class WasapiAudioEndpointAvailabilityMonitor : IAudioEndpointAvailabilityMonitor
{
    public bool IsAvailable(string? endpointId) => IsEndpointActive(endpointId, ProbeEndpoint);

    internal static bool IsEndpointActive(string? endpointId, Func<string, bool> probe)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return false;
        }

        try
        {
            return probe(endpointId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool ProbeEndpoint(string endpointId)
    {
        using MMDeviceEnumerator enumerator = new();
        using MMDevice device = enumerator.GetDevice(endpointId);
        return device.State == DeviceState.Active;
    }
}
