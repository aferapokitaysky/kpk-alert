using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Service;

internal sealed class AudioWorkerClient : IAudioEndpointControl, IAudioEndpointAvailabilityMonitor, IAudioPlayer,
    IHostedService
{
    internal const string PipeName = "KGTA.Alert.AudioWorker.v1";
    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(300);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 6,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly EdgeAgentOptions _options;
    private readonly string _audioRoot;
    private readonly IAudioPlaybackEventHandler _eventHandler;
    private readonly EdgeAgentStatusProvider _status;
    private readonly ILogger<AudioWorkerClient> _logger;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly object _activeSync = new();
    private Guid? _activeCommandId;
    private Guid? _activeEpisodeAnchorId;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;

    public AudioWorkerClient(
        EdgeAgentOptions options,
        IAudioPlaybackEventHandler eventHandler,
        EdgeAgentStatusProvider status,
        ILogger<AudioWorkerClient> logger)
    {
        _options = options;
        _audioRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.AudioRootDirectory));
        _eventHandler = eventHandler;
        _status = status;
        _logger = logger;
    }

    public Guid? ActiveCommandId { get { lock (_activeSync) return _activeCommandId; } }
    public Guid? ActiveEpisodeAnchorId { get { lock (_activeSync) return _activeEpisodeAnchorId; } }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _monitorTask = MonitorPlaybackAsync(_monitorCancellation.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_monitorCancellation is null) return;
        await _monitorCancellation.CancelAsync();
        if (_monitorTask is not null)
        {
            try { await _monitorTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (_monitorCancellation.IsCancellationRequested) { }
        }
        _monitorCancellation.Dispose();
    }

    public IReadOnlyList<EdgeAgentAudioEndpoint> GetActiveOutputs()
    {
        AudioWorkerResponse response = Send(new AudioWorkerRequest(1, Guid.NewGuid(), "get_audio_outputs"),
            CancellationToken.None).GetAwaiter().GetResult();
        if (!response.Succeeded || response.AudioEndpoints is null)
        {
            throw new AudioWorkerUnavailableException(response.DiagnosticCode ?? "audio_worker_unavailable");
        }

        return response.AudioEndpoints;
    }

    public async Task PlayTestSoundAsync(string endpointId, AudioTestSound sound, CancellationToken cancellationToken)
    {
        AudioWorkerResponse response = await Send(new AudioWorkerRequest(
            1, Guid.NewGuid(), "test_audio_output", AudioEndpointId: endpointId, TestSound: sound), cancellationToken);
        EnsureSuccess(response);
    }

    public bool IsAvailable(string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId)) return false;
        try
        {
            return GetActiveOutputs().Any(endpoint =>
                string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal));
        }
        catch (AudioWorkerUnavailableException)
        {
            return false;
        }
    }

    public async Task StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        await StartCommandAsync(request, 1, Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions))), cancellationToken);
    }

    public async Task StartCommandAsync(
        PlaybackRequest request,
        long sequence,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (ActiveCommandId is not null)
            {
                throw new AudioPlaybackException("audio_output_busy", new InvalidOperationException());
            }

            AudioWorkerResponse response = await Send(CreatePlaybackRequest(
                "start", request, request.CommandId, sequence, payloadHash), cancellationToken);
            EnsureSuccess(response);
            SetActive(request.CommandId, request.CommandId);
            UpdateAudioStatus("playing", request.CommandId, null);
        }
        catch (AudioWorkerUnavailableException exception)
        {
            UpdateAudioStatus("unavailable", null, "audio_worker_unavailable");
            throw new AudioPlaybackException("audio_worker_unavailable", exception);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task ReplaceCommandAsync(
        Guid episodeAnchorCommandId,
        PlaybackRequest request,
        long sequence,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            if (ActiveCommandId is not null && ActiveEpisodeAnchorId != episodeAnchorCommandId)
            {
                throw new AudioPlaybackException("replace_target_mismatch", new InvalidOperationException());
            }

            AudioWorkerResponse response = await Send(CreatePlaybackRequest(
                "replace", request, episodeAnchorCommandId, sequence, payloadHash), cancellationToken);
            EnsureSuccess(response);
            SetActive(request.CommandId, episodeAnchorCommandId);
            UpdateAudioStatus("playing", request.CommandId, null);
        }
        catch (AudioWorkerUnavailableException exception)
        {
            UpdateAudioStatus("unavailable", null, "audio_worker_unavailable");
            throw new AudioPlaybackException("audio_worker_unavailable", exception);
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async Task<bool> StopAsync(Guid targetCommandId, CancellationToken cancellationToken)
    {
        AudioWorkerResponse response = await Send(new AudioWorkerRequest(
            1, Guid.NewGuid(), "stop_active", EpisodeAnchorCommandId: targetCommandId), cancellationToken);
        EnsureSuccess(response);
        return response.ActiveCommandId is null;
    }

    public async Task<bool> StopCommandAsync(
        Guid targetCommandId,
        Guid commandId,
        long sequence,
        string payloadHash,
        CancellationToken cancellationToken)
    {
        try
        {
            AudioWorkerResponse response = await Send(new AudioWorkerRequest(
                1, Guid.NewGuid(), "stop", CommandId: commandId, EpisodeAnchorCommandId: targetCommandId,
                Sequence: sequence, PayloadHash: payloadHash), cancellationToken);
            EnsureSuccess(response);
            return response.DiagnosticCode != "stop_target_pending";
        }
        catch (AudioWorkerUnavailableException exception)
        {
            throw new AudioPlaybackException("audio_worker_unavailable", exception);
        }
    }

    public async Task ConfirmStartedAsync(Guid commandId, CancellationToken cancellationToken)
    {
        AudioWorkerResponse response = await Send(new AudioWorkerRequest(
            1, Guid.NewGuid(), "confirm_started", CommandId: commandId), cancellationToken);
        EnsureSuccess(response);
    }

    public async Task AbortStartAsync(Guid commandId, CancellationToken cancellationToken)
    {
        try
        {
            AudioWorkerResponse response = await Send(new AudioWorkerRequest(
                1, Guid.NewGuid(), "abort_start", CommandId: commandId), cancellationToken);
            EnsureSuccess(response);
        }
        catch (AudioWorkerUnavailableException)
        {
            // Процес worker уже недоступний, отже його аудіопотік не може бути відновлений.
        }
    }

    public async Task RecoverAfterServiceRestartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Send(new AudioWorkerRequest(1, Guid.NewGuid(), "recover_after_service_restart"), cancellationToken);
            SetActive(null, null);
        }
        catch (AudioWorkerUnavailableException)
        {
            // Worker може запуститися пізніше; повторне відтворення команд йому не передається.
        }
    }

    private async Task MonitorPlaybackAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (ActiveCommandId is null)
            {
                await Task.Delay(PollInterval, cancellationToken);
                continue;
            }

            try
            {
                AudioWorkerResponse response = await Send(
                    new AudioWorkerRequest(1, Guid.NewGuid(), "get_playback_event"), cancellationToken);
                EnsureSuccess(response);
                if (response.PlaybackEvent is { } playbackEvent)
                {
                    await _eventHandler.OnPlaybackFinishedAsync(playbackEvent.CommandId, playbackEvent.State,
                        playbackEvent.DiagnosticCode, cancellationToken);
                    EnsureSuccess(await Send(new AudioWorkerRequest(1, Guid.NewGuid(), "ack_playback_event",
                        CommandId: playbackEvent.CommandId), cancellationToken));
                    await _stateGate.WaitAsync(cancellationToken);
                    try
                    {
                        if (ActiveCommandId == playbackEvent.CommandId)
                        {
                            SetActive(null, null);
                            UpdateAudioStatus("idle", playbackEvent.CommandId, playbackEvent.DiagnosticCode);
                        }
                        else if (ActiveCommandId is { } activeCommandId)
                        {
                            UpdateAudioStatus("playing", activeCommandId, null);
                        }
                    }
                    finally { _stateGate.Release(); }
                }
                else
                {
                    await Task.Delay(PollInterval, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (AudioWorkerUnavailableException)
            {
                await FailActivePlaybackAsync(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogError("Не вдалося зберегти подію аудіо: {ExceptionType}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    private async Task FailActivePlaybackAsync(CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken);
        try
        {
            Guid? commandId = ActiveCommandId;
            if (commandId is null) return;
            await _eventHandler.OnPlaybackFinishedAsync(commandId.Value, DeviceCommandState.Failed,
                "audio_worker_unavailable", cancellationToken);
            SetActive(null, null);
            UpdateAudioStatus("unavailable", commandId, "audio_worker_unavailable");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError("Не вдалося зафіксувати втрату аудіопроцесу: {ExceptionType}.", exception.GetType().Name);
        }
        finally { _stateGate.Release(); }
    }

    private AudioWorkerRequest CreatePlaybackRequest(
        string operation,
        PlaybackRequest request,
        Guid episodeAnchorCommandId,
        long sequence,
        string payloadHash)
    {
        if (request.DeadlineUtc.Offset != TimeSpan.Zero || request.DeadlineUtc <= DateTimeOffset.UtcNow ||
            sequence < 1 || payloadHash.Length != 64 ||
            !payloadHash.All(Uri.IsHexDigit))
        {
            throw new AudioPlaybackException("command_expired", new InvalidDataException());
        }

        var relativePath = Path.GetRelativePath(_audioRoot, Path.GetFullPath(request.FilePath));
        if (Path.IsPathRooted(relativePath) || relativePath == ".." ||
            relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            relativePath.Length is 0 or > 1024)
        {
            throw new AudioPlaybackException("preset_missing", new InvalidDataException());
        }

        return new AudioWorkerRequest(1, Guid.NewGuid(), operation, request.CommandId,
            episodeAnchorCommandId, sequence, payloadHash, request.PresetCode, relativePath,
            request.Sha256, request.DeadlineUtc, request.AudioEndpointId);
    }

    private async Task<AudioWorkerResponse> Send(AudioWorkerRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var activeSessionId = GetActiveConsoleSessionId();
            if (activeSessionId == 0 || activeSessionId == uint.MaxValue)
            {
                throw new AudioWorkerUnavailableException("audio_worker_unavailable");
            }

            using NamedPipeClientStream pipe = new(".", $"{PipeName}.{activeSessionId}",
                PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(ConnectTimeout, cancellationToken);
            ValidatePipeServer(pipe);
            var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
            if (requestBytes.Length is <= 0 or > MaximumRequestBytes)
            {
                throw new AudioWorkerUnavailableException("audio_worker_protocol_invalid");
            }

            await WriteFrameAsync(pipe, requestBytes, cancellationToken);
            var responseBytes = await ReadFrameAsync(pipe, MaximumResponseBytes, cancellationToken);
            AudioWorkerResponse? response = JsonSerializer.Deserialize<AudioWorkerResponse>(responseBytes, JsonOptions);
            if (response is null || response.SchemaVersion != 1 || response.RequestId != request.RequestId)
            {
                throw new AudioWorkerUnavailableException("audio_worker_protocol_invalid");
            }

            return response;
        }
        catch (AudioWorkerUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                           UnauthorizedAccessException or Win32Exception or InvalidOperationException or
                                           TimeoutException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Користувацький аудіопроцес недоступний: {ExceptionType}.", exception.GetType().Name);
            throw new AudioWorkerUnavailableException("audio_worker_unavailable", exception);
        }
    }

    private void ValidatePipeServer(NamedPipeClientStream pipe)
    {
        if (!OperatingSystem.IsWindows() || _options.InteractiveUserSid is null ||
            !GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var processId) || processId == 0)
        {
            throw new AudioWorkerUnavailableException("audio_worker_identity_invalid");
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            var imagePath = process.MainModule?.FileName;
            var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "Watcher.EdgeAgent.AudioWorker.exe"));
            if (process.SessionId == 0 || process.SessionId != GetActiveConsoleSessionId() || imagePath is null ||
                !string.Equals(Path.GetFullPath(imagePath), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                GetProcessUserSid(process) != _options.InteractiveUserSid)
            {
                throw new AudioWorkerUnavailableException("audio_worker_identity_invalid");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception)
        {
            throw new AudioWorkerUnavailableException("audio_worker_identity_invalid", exception);
        }
    }

    private static string? GetProcessUserSid(Process process)
    {
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token)) return null;
        try
        {
            using WindowsIdentity identity = new(token);
            return identity.User?.Value;
        }
        finally { CloseHandle(token); }
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes) throw new InvalidDataException();
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static void EnsureSuccess(AudioWorkerResponse response)
    {
        if (!response.Succeeded)
        {
            var code = response.DiagnosticCode ?? "audio_worker_unavailable";
            if (code == "audio_worker_unavailable" || code.StartsWith("audio_worker_", StringComparison.Ordinal))
            {
                throw new AudioWorkerUnavailableException(code);
            }

            throw new AudioPlaybackException(code, new InvalidOperationException());
        }
    }

    private void UpdateAudioStatus(string audioState, Guid? commandId, string? diagnosticCode) => _status.Update(current => current with
    {
        AudioState = audioState,
        LastCommandId = commandId ?? current.LastCommandId,
        LastCommandState = audioState == "playing" ? DeviceCommandState.Playing.ToString() : current.LastCommandState,
        DiagnosticCode = diagnosticCode
    });

    private void SetActive(Guid? commandId, Guid? episodeAnchorId)
    {
        lock (_activeSync)
        {
            _activeCommandId = commandId;
            _activeEpisodeAnchorId = episodeAnchorId;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TokenQuery = 0x0008;
}

internal sealed class AudioWorkerUnavailableException(string diagnosticCode, Exception? innerException = null)
    : Exception("Користувацький аудіопроцес недоступний.", innerException)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}
