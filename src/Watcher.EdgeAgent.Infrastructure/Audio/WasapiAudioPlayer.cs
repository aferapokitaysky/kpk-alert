using System.IO;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Audio;

public sealed class WasapiAudioPlayer : IAudioPlayer, IAsyncDisposable
{
    private readonly string _audioRoot;
    private readonly IAudioPlaybackEventHandler _eventHandler;
    private readonly ILogger<WasapiAudioPlayer> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private PlaybackSession? _active;

    public WasapiAudioPlayer(
        string audioRootDirectory,
        IAudioPlaybackEventHandler eventHandler,
        ILogger<WasapiAudioPlayer> logger)
    {
        _audioRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(audioRootDirectory));
        _eventHandler = eventHandler;
        _logger = logger;
    }

    public Guid? ActiveCommandId => _active?.Request.CommandId;

    public async Task StartAsync(PlaybackRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is not null)
            {
                throw new AudioPlaybackException("audio_output_busy", new InvalidOperationException());
            }

            TimeSpan remaining = request.DeadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new AudioPlaybackException("command_expired", new TimeoutException());
            }

            if (string.IsNullOrWhiteSpace(request.AudioEndpointId))
            {
                throw new AudioPlaybackException("audio_output_unavailable", new InvalidOperationException());
            }

            var filePath = ValidateAudioPath(request.FilePath);
            using (FileStream validationStream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (!PcmWaveFileValidator.IsSupported(validationStream, out _))
                {
                    throw new AudioPlaybackException("audio_format_unsupported", new InvalidDataException());
                }
            }

            PlaybackSession? session = null;
            MMDeviceEnumerator? enumerator = null;
            MMDevice? device = null;
            WaveFileReader? reader = null;
            WasapiOut? output = null;
            try
            {
                enumerator = new MMDeviceEnumerator();
                try
                {
                    device = enumerator.GetDevice(request.AudioEndpointId);
                }
                catch (Exception exception)
                {
                    throw new AudioPlaybackException("audio_output_unavailable", exception);
                }

                reader = new WaveFileReader(filePath);
                output = new WasapiOut(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
                session = new PlaybackSession(request, enumerator, device, reader, output);
                output.PlaybackStopped += session.OnPlaybackStopped;
                output.Init(reader);
                _active = session;
                output.Play();
            }
            catch (AudioPlaybackException)
            {
                if (session is not null)
                {
                    session.Dispose();
                }
                else
                {
                    output?.Dispose();
                    reader?.Dispose();
                    device?.Dispose();
                    enumerator?.Dispose();
                }

                throw;
            }
            catch (Exception exception)
            {
                if (session is not null)
                {
                    session.Dispose();
                }
                else
                {
                    output?.Dispose();
                    reader?.Dispose();
                    device?.Dispose();
                    enumerator?.Dispose();
                }

                throw new AudioPlaybackException("audio_start_failed", exception);
            }

            _ = MonitorPlaybackAsync(session!, remaining);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> StopAsync(Guid targetCommandId, CancellationToken cancellationToken)
    {
        PlaybackSession? session;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            session = _active;
            if (session is null || session.Request.CommandId != targetCommandId)
            {
                return false;
            }

            session.TrySetFinishState(DeviceCommandState.Stopped, null);
            session.StopRequested.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }

        await session.Finished.Task.WaitAsync(cancellationToken);
        return true;
    }

    public async Task AbortStartAsync(Guid commandId, CancellationToken cancellationToken)
    {
        PlaybackSession? session;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            session = _active;
            if (session is null || session.Request.CommandId != commandId)
            {
                return;
            }

            session.TrySetFinishState(DeviceCommandState.Failed, "command_state_persistence_failed");
            session.StatePersisted.TrySetResult();
            session.StopRequested.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }

        await session.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task ConfirmStartedAsync(Guid commandId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_active is null || _active.Request.CommandId != commandId)
            {
                throw new InvalidOperationException("Активну аудіокоманду не знайдено.");
            }

            _active.StatePersisted.TrySetResult();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        PlaybackSession? session;
        await _gate.WaitAsync();
        try
        {
            session = _active;
            if (session is not null)
            {
                session.TrySetFinishState(DeviceCommandState.Failed, "agent_stopping");
                session.StatePersisted.TrySetResult();
                session.StopRequested.TrySetResult();
            }
        }
        finally
        {
            _gate.Release();
        }

        if (session is not null)
        {
            await session.Finished.Task;
        }

        _gate.Dispose();
    }

    private async Task MonitorPlaybackAsync(PlaybackSession session, TimeSpan remaining)
    {
        try
        {
            var deadline = Task.Delay(remaining);
            Task signal = await Task.WhenAny(
                session.StatePersisted.Task,
                session.PlaybackStopped.Task,
                session.StopRequested.Task,
                deadline);
            if (signal == session.StatePersisted.Task)
            {
                signal = await Task.WhenAny(session.PlaybackStopped.Task, session.StopRequested.Task, deadline);
            }

            if (signal == deadline)
            {
                var statePersisted = session.StatePersisted.Task.IsCompleted;
                session.TrySetFinishState(
                    statePersisted ? DeviceCommandState.Completed : DeviceCommandState.Failed,
                    statePersisted ? null : "command_state_persistence_timeout");
                session.StopRequested.TrySetResult();
            }
            else if (signal == session.PlaybackStopped.Task && !session.StatePersisted.Task.IsCompleted)
            {
                Task stateSignal = await Task.WhenAny(
                    session.StatePersisted.Task, session.StopRequested.Task, deadline);
                if (stateSignal == deadline)
                {
                    session.TrySetFinishState(DeviceCommandState.Failed, "command_state_persistence_timeout");
                    session.StopRequested.TrySetResult();
                }
            }

            if (session.StopRequested.Task.IsCompleted && !session.PlaybackStopped.Task.IsCompleted)
            {
                try
                {
                    session.Output.Stop();
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Не вдалося зупинити аудіопотік {CommandId}.",
                        session.Request.CommandId);
                }
            }

            StoppedEventArgs stopEvent;
            try
            {
                stopEvent = await session.PlaybackStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (TimeoutException)
            {
                await _eventHandler.OnPlaybackFinishedAsync(
                    session.Request.CommandId,
                    DeviceCommandState.Failed,
                    "audio_stop_timeout",
                    CancellationToken.None);
                return;
            }

            DeviceCommandState state = session.FinishState ??
                (stopEvent.Exception is null ? DeviceCommandState.Completed : DeviceCommandState.Failed);
            var diagnosticCode = session.FinishDiagnosticCode ??
                (stopEvent.Exception is null ? null : "audio_output_unavailable");

            await _eventHandler.OnPlaybackFinishedAsync(
                session.Request.CommandId, state, diagnosticCode, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Помилка завершення аудіокоманди {CommandId}.", session.Request.CommandId);
            try
            {
                await _eventHandler.OnPlaybackFinishedAsync(
                    session.Request.CommandId, DeviceCommandState.Failed, "audio_output_unavailable", CancellationToken.None);
            }
            catch (Exception handlerException)
            {
                _logger.LogError(handlerException, "Не вдалося зберегти фінальний стан аудіокоманди {CommandId}.",
                    session.Request.CommandId);
            }
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (ReferenceEquals(_active, session))
                {
                    _active = null;
                }
            }
            finally
            {
                _gate.Release();
            }

            session.Dispose();
            session.Finished.TrySetResult();
        }
    }

    private string ValidateAudioPath(string requestedPath)
    {
        var fullPath = Path.GetFullPath(requestedPath);
        var rootPrefix = _audioRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith("\\\\", StringComparison.Ordinal) ||
            !File.Exists(fullPath))
        {
            throw new AudioPlaybackException("preset_missing", new FileNotFoundException());
        }

        var current = _audioRoot;
        if (HasReparsePoint(current))
        {
            throw new AudioPlaybackException("preset_missing", new IOException());
        }

        foreach (var segment in Path.GetRelativePath(_audioRoot, fullPath)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (HasReparsePoint(current))
            {
                throw new AudioPlaybackException("preset_missing", new IOException());
            }
        }

        return fullPath;
    }

    private static bool HasReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private sealed class PlaybackSession(
        PlaybackRequest request,
        MMDeviceEnumerator enumerator,
        MMDevice device,
        WaveFileReader reader,
        WasapiOut output) : IDisposable
    {
        private int _finishState = -1;
        private string? _finishDiagnosticCode;

        public PlaybackRequest Request { get; } = request;
        public MMDeviceEnumerator Enumerator { get; } = enumerator;
        public MMDevice Device { get; } = device;
        public WaveFileReader Reader { get; } = reader;
        public WasapiOut Output { get; } = output;
        public TaskCompletionSource<StoppedEventArgs> PlaybackStopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StatePersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DeviceCommandState? FinishState => _finishState < 0 ? null : (DeviceCommandState)_finishState;
        public string? FinishDiagnosticCode => _finishDiagnosticCode;

        public void TrySetFinishState(DeviceCommandState state, string? diagnosticCode)
        {
            if (Interlocked.CompareExchange(ref _finishState, (int)state, -1) == -1)
            {
                _finishDiagnosticCode = diagnosticCode;
            }
        }

        public void OnPlaybackStopped(object? sender, StoppedEventArgs args) => PlaybackStopped.TrySetResult(args);

        public void Dispose()
        {
            Output.PlaybackStopped -= OnPlaybackStopped;
            Output.Dispose();
            Reader.Dispose();
            Device.Dispose();
            Enumerator.Dispose();
        }
    }
}
