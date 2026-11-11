using System.Text.Json;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Service;

internal sealed class EdgeAgentAudioOutputManager : IAudioEndpointSelection
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _settingsPath;
    private readonly IAudioEndpointControl _endpointControl;
    private readonly EdgeAgentStatusProvider _status;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _currentEndpointId;

    public EdgeAgentAudioOutputManager(
        string? initialEndpointId,
        string settingsPath,
        IAudioEndpointControl endpointControl,
        EdgeAgentStatusProvider status)
    {
        _settingsPath = Path.GetFullPath(settingsPath);
        _endpointControl = endpointControl;
        _status = status;
        if (TryReadSavedEndpointId(out var savedEndpointId))
        {
            _currentEndpointId = savedEndpointId;
            HasInvalidSavedSettings = _currentEndpointId is null;
        }
        else
        {
            _currentEndpointId = initialEndpointId;
        }
    }

    public string? CurrentEndpointId => Volatile.Read(ref _currentEndpointId);
    public bool HasInvalidSavedSettings { get; private set; }

    public IReadOnlyList<EdgeAgentAudioEndpoint> GetActiveOutputs() => _endpointControl.GetActiveOutputs();

    public async Task<string?> SetEndpointAsync(string? endpointId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 512)
        {
            return "audio_output_invalid";
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var endpointExists = _endpointControl.GetActiveOutputs().Any(endpoint =>
                string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal));
            if (!endpointExists)
            {
                return "audio_output_unavailable";
            }

            SaveEndpointId(endpointId);
            Volatile.Write(ref _currentEndpointId, endpointId);
            _status.Update(current => current with
            {
                AudioEndpointId = endpointId,
                AudioState = "ready",
                DiagnosticCode = current.DiagnosticCode == "audio_output_configuration_invalid"
                    ? null
                    : current.DiagnosticCode
            });
            HasInvalidSavedSettings = false;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task TestOutputAsync(
        string? endpointId,
        AudioTestSound sound,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointId) || endpointId.Length > 512)
        {
            throw new InvalidOperationException("Виберіть доступний аудіовихід.");
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await _endpointControl.PlayTestSoundAsync(endpointId, sound, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryReadSavedEndpointId(out string? endpointId)
    {
        endpointId = null;
        try
        {
            using FileStream settings = File.OpenRead(_settingsPath);
            if (settings.Length > 4096) return true;

            AudioEndpointSettings? saved = JsonSerializer.Deserialize<AudioEndpointSettings>(settings, JsonOptions);
            endpointId = saved is { SchemaVersion: 1, AudioEndpointId.Length: > 0 and <= 512 }
                ? saved.AudioEndpointId
                : null;
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return true;
        }
    }

    private void SaveEndpointId(string endpointId)
    {
        var directory = Path.GetDirectoryName(_settingsPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $"audio-endpoint.{Guid.NewGuid():N}.tmp");
        try
        {
            using (FileStream settings = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(settings, new AudioEndpointSettings(1, endpointId), JsonOptions);
                settings.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _settingsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private sealed record AudioEndpointSettings(int SchemaVersion, string AudioEndpointId);
}
