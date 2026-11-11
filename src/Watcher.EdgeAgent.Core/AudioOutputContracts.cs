using Watcher.EdgeAgent.Contracts;

namespace Watcher.EdgeAgent.Core;

public interface IAudioEndpointSelection
{
    string? CurrentEndpointId { get; }
}

public interface IAudioEndpointControl
{
    IReadOnlyList<EdgeAgentAudioEndpoint> GetActiveOutputs();
    Task PlayTestSoundAsync(
        string endpointId,
        AudioTestSound sound,
        CancellationToken cancellationToken);
}
