using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;

namespace Watcher.EdgeAgent.AudioWorker;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 6,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 2;
        }

        if (args.Length > 0 && args[0] == "--supervise")
        {
            return await RunSupervisorAsync(args[1..]);
        }

        if (args.Length > 0 && args[0] == "--worker")
        {
            args = args[1..];
        }

        if (!TryReadArguments(args, out var audioRoot, out var expectedSid)) return 2;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var process = Process.GetCurrentProcess();
            if (identity.User?.Value != expectedSid ||
                !AudioWorkerPipeServer.IsAllowedSession(process.SessionId, GetActiveConsoleSessionId()) ||
                !Directory.Exists(audioRoot))
            {
                return 3;
            }

            audioRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(audioRoot));
            AudioWorkerPlaybackEventQueue playbackEvents = new();
            await using WasapiAudioPlayer player = new(audioRoot, playbackEvents,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<WasapiAudioPlayer>.Instance);
            AudioWorkerPipeServer server = new(player, new WasapiAudioEndpointControl(), playbackEvents,
                audioRoot, process.SessionId);
            await server.RunAsync(CancellationToken.None);
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Trace.TraceError("Audio worker зупинився: {0}.", exception.GetType().Name);
            return 4;
        }
    }

    private static async Task<int> RunSupervisorAsync(string[] args)
    {
        if (!TryReadArguments(args, out var audioRoot, out var expectedSid)) return 2;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            using var supervisor = Process.GetCurrentProcess();
            if (identity.User?.Value != expectedSid ||
                !AudioWorkerPipeServer.IsAllowedSession(supervisor.SessionId, GetActiveConsoleSessionId()) ||
                !Directory.Exists(audioRoot))
            {
                return 3;
            }

            var executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Не вдалося визначити шлях supervisor-процесу.");
            await RunSupervisionLoopAsync(async cancellationToken =>
            {
                ProcessStartInfo startInfo = new(executablePath)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!
                };
                startInfo.ArgumentList.Add("--worker");
                startInfo.ArgumentList.Add("--audio-root");
                startInfo.ArgumentList.Add(audioRoot!);
                startInfo.ArgumentList.Add("--user-sid");
                startInfo.ArgumentList.Add(expectedSid!);

                using Process worker = Process.Start(startInfo)
                    ?? throw new InvalidOperationException("Windows не запустила аудіопроцес.");
                await worker.WaitForExitAsync(cancellationToken);
                Trace.TraceWarning("Audio worker завершився з кодом {0}; запуск буде повторено.", worker.ExitCode);
            }, Task.Delay, CancellationToken.None);
            return 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or
                                           System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Trace.TraceError("Supervisor audio worker зупинився: {0}.", exception.GetType().Name);
            return 4;
        }
    }

    internal static async Task RunSupervisionLoopAsync(
        Func<CancellationToken, Task> runWorker,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        var failureCount = 0;
        while (true)
        {
            DateTimeOffset startedAt = DateTimeOffset.UtcNow;
            try
            {
                await runWorker(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Trace.TraceError("Не вдалося запустити audio worker: {0}.", exception.GetType().Name);
            }

            if (DateTimeOffset.UtcNow - startedAt >= TimeSpan.FromMinutes(1)) failureCount = 0;
            await delay(GetRestartDelay(failureCount++), cancellationToken);
        }
    }

    internal static TimeSpan GetRestartDelay(int failureCount)
    {
        var exponent = Math.Clamp(failureCount, 0, 5);
        return TimeSpan.FromSeconds(Math.Min(30, 1 << exponent));
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetActiveConsoleSessionId();

    private static bool TryReadArguments(string[] args, out string? audioRoot, out string? expectedSid)
    {
        audioRoot = null;
        expectedSid = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--audio-root" && index + 1 < args.Length)
            {
                audioRoot = args[++index];
            }
            else if (args[index] == "--user-sid" && index + 1 < args.Length)
            {
                expectedSid = args[++index];
            }
            else
            {
                return false;
            }
        }

        return !string.IsNullOrWhiteSpace(audioRoot) &&
            expectedSid is not null &&
            System.Text.RegularExpressions.Regex.IsMatch(expectedSid,
                @"^S-1-(?:5-21-(?:\d+-){3}\d+|12-1-(?:\d+-){3}\d+)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}

internal sealed class AudioWorkerPlaybackEventQueue : IAudioPlaybackEventHandler
{
    private const int MaximumEvents = 32;
    private readonly Queue<AudioWorkerPlaybackEvent> _events = new();
    private readonly object _gate = new();

    public Task OnPlaybackFinishedAsync(Guid commandId, DeviceCommandState state,
        string? diagnosticCode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_events.Count >= MaximumEvents)
            {
                throw new InvalidOperationException("Черга аудіоподій worker переповнена.");
            }
            _events.Enqueue(new AudioWorkerPlaybackEvent(commandId, state, diagnosticCode));
        }
        return Task.CompletedTask;
    }

    public AudioWorkerPlaybackEvent? PeekNext()
    {
        lock (_gate) return _events.TryPeek(out AudioWorkerPlaybackEvent? item) ? item : null;
    }

    public bool Acknowledge(Guid commandId)
    {
        lock (_gate)
        {
            if (!_events.TryPeek(out AudioWorkerPlaybackEvent? item) || item.CommandId != commandId) return false;
            _events.Dequeue();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate) _events.Clear();
    }
}

internal sealed class AudioWorkerCommandRegistry
{
    internal const int MaximumRememberedCommands = 10_000;
    private readonly Dictionary<Guid, (string PayloadHash, long Sequence)> _commands = [];
    private readonly Queue<Guid> _commandOrder = new();
    private long _lastSequence;

    internal int RememberedCommandCount => _commands.Count;

    // Довготривалу перевірку ID і хешу виконує SQLite служби; цей обмежений кеш зберігає недавні повтори.
    // Монотонний номер відхиляє старі команди після витіснення їхніх ID.
    public bool TryRegister(Guid commandId, long sequence, string payloadHash,
        bool allowExactStopRetry, out string? diagnosticCode)
    {
        if (_commands.TryGetValue(commandId, out (string PayloadHash, long Sequence) existing))
        {
            if (!string.Equals(existing.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                diagnosticCode = "command_id_conflict";
                return false;
            }

            diagnosticCode = allowExactStopRetry ? null : "duplicate_command";
            return allowExactStopRetry;
        }

        if (sequence <= _lastSequence)
        {
            diagnosticCode = "stale_sequence";
            return false;
        }

        if (_commands.Count == MaximumRememberedCommands)
        {
            Guid oldestCommandId = _commandOrder.Dequeue();
            _commands.Remove(oldestCommandId);
        }

        _commands.Add(commandId, (payloadHash, sequence));
        _commandOrder.Enqueue(commandId);
        _lastSequence = sequence;
        diagnosticCode = null;
        return true;
    }
}

internal sealed class AudioWorkerPipeServer(
    WasapiAudioPlayer player,
    WasapiAudioEndpointControl endpoints,
    AudioWorkerPlaybackEventQueue events,
    string audioRoot,
    int workerSessionId)
{
    private const int MaximumRequestBytes = 16 * 1024;
    private const int MaximumResponseBytes = 64 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ServiceHeartbeatTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 6,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _audioRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(audioRoot));
    private readonly AudioWorkerCommandRegistry _commands = new();
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private long _lastServiceContact = Stopwatch.GetTimestamp();

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var watchdogCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task watchdog = WatchServiceAsync(watchdogCancellation.Token);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using NamedPipeServerStream pipe = CreatePipe(workerSessionId);
                await pipe.WaitForConnectionAsync(cancellationToken);
                if (!IsAuthorizedService(pipe))
                {
                    continue;
                }

                Interlocked.Exchange(ref _lastServiceContact, Stopwatch.GetTimestamp());
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestCancellation.CancelAfter(RequestTimeout);
                AudioWorkerRequest? request = null;
                AudioWorkerResponse response;
                try
                {
                    var requestBytes = await ReadFrameAsync(pipe, MaximumRequestBytes, requestCancellation.Token);
                    request = JsonSerializer.Deserialize<AudioWorkerRequest>(requestBytes, JsonOptions);
                    response = request is null
                        ? Failed(Guid.Empty, "invalid_request")
                        : await HandleAsync(request, requestCancellation.Token);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException or JsonException or InvalidDataException or
                                                   UnauthorizedAccessException or AudioPlaybackException or InvalidOperationException)
                {
                    response = Failed(request?.RequestId ?? Guid.Empty,
                        exception is AudioPlaybackException audio ? audio.DiagnosticCode : "invalid_request");
                }

                var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response, JsonOptions);
                if (responseBytes.Length <= MaximumResponseBytes)
                {
                    await WriteFrameAsync(pipe, responseBytes, cancellationToken);
                }
            }
        }
        finally
        {
            watchdogCancellation.Cancel();
            try { await watchdog; }
            catch (OperationCanceledException) { }
        }
    }

    private async Task<AudioWorkerResponse> HandleAsync(AudioWorkerRequest request, CancellationToken cancellationToken)
    {
        if (request.SchemaVersion != 1 || request.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(request.Operation))
        {
            return Failed(request.RequestId, "invalid_request");
        }

        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            switch (request.Operation)
            {
                case "get_audio_outputs":
                    return Success(request.RequestId, AudioEndpoints: endpoints.GetActiveOutputs());
                case "test_audio_output" when request.AudioEndpointId is { Length: > 0 and <= 512 } &&
                                                   request.TestSound is { } sound && Enum.IsDefined(sound):
                    await endpoints.PlayTestSoundAsync(request.AudioEndpointId, sound, cancellationToken);
                    return Success(request.RequestId);
                case "start":
                case "replace":
                    return await StartOrReplaceAsync(request, cancellationToken);
                case "stop":
                    return await StopAsync(request, cancellationToken);
                case "stop_active" when request.EpisodeAnchorCommandId is { } anchor && anchor != Guid.Empty:
                    _ = await player.StopAsync(anchor, cancellationToken);
                    return Success(request.RequestId, ActiveCommandId: player.ActiveCommandId,
                        ActiveEpisodeAnchorCommandId: player.ActiveEpisodeAnchorId);
                case "confirm_started" when request.CommandId is { } confirmId:
                    await player.ConfirmStartedAsync(confirmId, cancellationToken);
                    return Success(request.RequestId, ActiveCommandId: player.ActiveCommandId,
                        ActiveEpisodeAnchorCommandId: player.ActiveEpisodeAnchorId);
                case "abort_start" when request.CommandId is { } abortId:
                    await player.AbortStartAsync(abortId, cancellationToken);
                    return Success(request.RequestId, ActiveCommandId: player.ActiveCommandId,
                        ActiveEpisodeAnchorCommandId: player.ActiveEpisodeAnchorId);
                case "get_playback_event":
                    return Success(request.RequestId, ActiveCommandId: player.ActiveCommandId,
                        ActiveEpisodeAnchorCommandId: player.ActiveEpisodeAnchorId,
                        PlaybackEvent: events.PeekNext());
                case "ack_playback_event" when request.CommandId is { } eventCommandId:
                    return events.Acknowledge(eventCommandId)
                        ? Success(request.RequestId)
                        : Failed(request.RequestId, "invalid_request");
                case "recover_after_service_restart":
                    if (player.ActiveEpisodeAnchorId is { } activeAnchor)
                    {
                        await player.FailAsync(activeAnchor, "agent_restart", cancellationToken);
                    }
                    events.Clear();
                    return Success(request.RequestId);
                default:
                    return Failed(request.RequestId, "invalid_request");
            }
        }
        finally { _commandGate.Release(); }
    }

    private async Task<AudioWorkerResponse> StartOrReplaceAsync(AudioWorkerRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CommandId is not { } commandId || commandId == Guid.Empty ||
            request.EpisodeAnchorCommandId is not { } episodeAnchor || episodeAnchor == Guid.Empty ||
            request.Sequence is not { } sequence || sequence < 1 ||
            request.PayloadHash is not { Length: 64 } payloadHash || !payloadHash.All(Uri.IsHexDigit) ||
            request.PresetCode is not { Length: > 0 and <= 80 } presetCode ||
            request.Sha256 is not { Length: 64 } sha256 || !sha256.All(Uri.IsHexDigit) ||
            request.DeadlineUtc is not { } deadline || deadline.Offset != TimeSpan.Zero ||
            deadline <= DateTimeOffset.UtcNow ||
            request.AudioEndpointId is not { Length: > 0 and <= 512 } endpointId ||
            request.AudioFileRelativePath is not { Length: > 0 and <= 1024 } relativePath)
        {
            return Failed(request.RequestId, "invalid_request");
        }

        if (!_commands.TryRegister(commandId, sequence, payloadHash, allowExactStopRetry: false,
                out var registrationError))
        {
            return Failed(request.RequestId, registrationError!);
        }

        var path = ResolveAudioFile(relativePath);
        if (path is null) return Failed(request.RequestId, "preset_missing");
        PlaybackRequest playback = new(commandId, presetCode, path, sha256, deadline, endpointId);
        if (request.Operation == "start")
        {
            if (episodeAnchor != commandId || player.ActiveCommandId is not null)
            {
                return Failed(request.RequestId, "audio_output_busy");
            }
            await player.StartAsync(playback, cancellationToken);
        }
        else if (request.Operation == "replace")
        {
            await player.ReplaceAsync(episodeAnchor, playback, cancellationToken);
        }
        else
        {
            return Failed(request.RequestId, "invalid_request");
        }

        return Success(request.RequestId, ActiveCommandId: player.ActiveCommandId,
            ActiveEpisodeAnchorCommandId: player.ActiveEpisodeAnchorId);
    }

    private async Task<AudioWorkerResponse> StopAsync(AudioWorkerRequest request, CancellationToken cancellationToken)
    {
        if (request.CommandId is not { } commandId || commandId == Guid.Empty ||
            request.EpisodeAnchorCommandId is not { } anchor || anchor == Guid.Empty ||
            request.Sequence is not { } sequence || sequence < 1 ||
            request.PayloadHash is not { Length: 64 } payloadHash || !payloadHash.All(Uri.IsHexDigit))
        {
            return Failed(request.RequestId, "invalid_request");
        }

        if (!_commands.TryRegister(commandId, sequence, payloadHash, allowExactStopRetry: true,
                out var registrationError))
        {
            return Failed(request.RequestId, registrationError!);
        }

        var stopped = await player.StopAsync(anchor, cancellationToken);
        return Success(request.RequestId, DiagnosticCode: stopped ? null : "stop_target_pending",
            ActiveCommandId: player.ActiveCommandId, ActiveEpisodeAnchorCommandId: player.ActiveEpisodeAnchorId);
    }

    private string? ResolveAudioFile(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Contains('\0')) return null;
        var segments = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]);
        if (segments.Any(segment => segment is "" or "." or "..")) return null;
        var path = Path.GetFullPath(Path.Combine(_audioRoot, relativePath));
        return path.StartsWith(_audioRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? path
            : null;
    }

    private async Task WatchServiceAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            if (player.ActiveEpisodeAnchorId is not { } anchor) continue;
            var lastContact = Interlocked.Read(ref _lastServiceContact);
            if (Stopwatch.GetElapsedTime(lastContact) < ServiceHeartbeatTimeout) continue;
            try { await player.FailAsync(anchor, "audio_worker_unavailable", cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                System.Diagnostics.Trace.TraceError("Worker не зупинив звук після втрати служби: {0}.",
                    exception.GetType().Name);
            }
        }
    }

    private bool IsAuthorizedService(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var processId) || processId == 0) return false;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var expectedPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "Watcher.EdgeAgent.Service.exe"));
            if (process.SessionId != 0 || process.MainModule?.FileName is not { } imagePath ||
                !string.Equals(Path.GetFullPath(imagePath), expectedPath, StringComparison.OrdinalIgnoreCase) ||
                GetProcessUserSid(process) != ((SecurityIdentifier)new NTAccount(
                    "NT SERVICE", "KGTA.Alert.EdgeAgent").Translate(typeof(SecurityIdentifier))).Value)
            {
                return false;
            }

            string? impersonatedSid = null;
            pipe.RunAsClient(() =>
            {
                using var client = WindowsIdentity.GetCurrent();
                impersonatedSid = client.User?.Value;
            });
            return impersonatedSid == GetProcessUserSid(process);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private NamedPipeServerStream CreatePipe(int sessionId)
    {
        var serviceIdentity = (SecurityIdentifier)new NTAccount(
            "NT SERVICE", "KGTA.Alert.EdgeAgent").Translate(typeof(SecurityIdentifier));
        PipeSecurity security = CreatePipeSecurity(serviceIdentity);
        var securityDescriptor = security.GetSecurityDescriptorBinaryForm();
        var descriptor = Marshal.AllocHGlobal(securityDescriptor.Length);
        try
        {
            Marshal.Copy(securityDescriptor, 0, descriptor, securityDescriptor.Length);
            SecurityAttributes attributes = new()
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false
            };
            SafePipeHandle pipeHandle = CreateNamedPipe(
                $"\\\\.\\pipe\\KGTA.Alert.AudioWorker.v1.{sessionId}",
                PipeAccessDuplex | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                1, MaximumResponseBytes, MaximumRequestBytes, 0, ref attributes);
            if (pipeHandle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                pipeHandle.Dispose();
                throw new System.ComponentModel.Win32Exception(error, "Не вдалося створити захищений audio worker pipe.");
            }

            try { return new NamedPipeServerStream(PipeDirection.InOut, true, false, pipeHandle); }
            catch
            {
                pipeHandle.Dispose();
                throw;
            }
        }
        finally { Marshal.FreeHGlobal(descriptor); }
    }

    internal static PipeSecurity CreatePipeSecurity(SecurityIdentifier serviceIdentity)
    {
        PipeSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(serviceIdentity, PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    internal static string GetPipeAccessSddl(SecurityIdentifier serviceIdentity) =>
        CreatePipeSecurity(serviceIdentity).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    internal static bool IsAllowedSession(int workerSessionId, uint activeConsoleSessionId) =>
        workerSessionId > 0 && activeConsoleSessionId != uint.MaxValue &&
        workerSessionId == activeConsoleSessionId;

    private static AudioWorkerResponse Success(Guid requestId,
        IReadOnlyList<EdgeAgentAudioEndpoint>? AudioEndpoints = null,
        Guid? ActiveCommandId = null,
        Guid? ActiveEpisodeAnchorCommandId = null,
        AudioWorkerPlaybackEvent? PlaybackEvent = null,
        string? DiagnosticCode = null) =>
        new(1, requestId, true, DiagnosticCode, AudioEndpoints, ActiveCommandId,
            ActiveEpisodeAnchorCommandId, PlaybackEvent);

    private static AudioWorkerResponse Failed(Guid requestId, string diagnosticCode) =>
        new(1, requestId, false, diagnosticCode);

    private static async Task<byte[]> ReadFrameAsync(Stream stream, int maximumBytes, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 || length > maximumBytes) throw new InvalidDataException();
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    private static async Task WriteFrameAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(int)];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateNamedPipeW")]
    private static extern SafePipeHandle CreateNamedPipe(
        string name,
        uint openMode,
        uint pipeMode,
        uint maximumInstances,
        uint outputBufferSize,
        uint inputBufferSize,
        uint defaultTimeout,
        ref SecurityAttributes securityAttributes);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private const uint TokenQuery = 0x0008;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
}
