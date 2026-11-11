using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Watcher.EdgeAgent.Contracts;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Audio;

public sealed class WasapiAudioEndpointControl : IAudioEndpointControl
{
    private const int MaximumEndpoints = 16;
    private const int MaximumEndpointIdLength = 512;
    private const int MaximumEndpointNameLength = 256;
    private const int TestToneDurationMilliseconds = 450;
    private const int TestNoteDurationMilliseconds = 220;
    private const int SampleRate = 24_000;
    private const double MaximumAmplitude = 0.12;

    public IReadOnlyList<EdgeAgentAudioEndpoint> GetActiveOutputs()
    {
        using MMDeviceEnumerator enumerator = new();
        MMDeviceCollection devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        string? defaultId = null;
        try
        {
            using MMDevice defaultDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            defaultId = defaultDevice.ID;
        }
        catch (COMException)
        {
            // Без типового виходу перелік активних пристроїв усе одно залишається доступним.
        }

        try
        {
            if (devices.Count > MaximumEndpoints)
            {
                throw new InvalidOperationException("Перелік аудіовиходів перевищує підтримуваний розмір.");
            }

            var endpoints = new List<EdgeAgentAudioEndpoint>(devices.Count);
            for (var index = 0; index < devices.Count; index++)
            {
                MMDevice device = devices[index];
                var id = device.ID;
                var name = device.FriendlyName;
                if (string.IsNullOrWhiteSpace(id) || id.Length > MaximumEndpointIdLength ||
                    string.IsNullOrWhiteSpace(name) || name.Length > MaximumEndpointNameLength)
                {
                    throw new InvalidOperationException("Ідентифікатор або назва аудіовиходу має непідтримуваний розмір.");
                }

                endpoints.Add(new EdgeAgentAudioEndpoint(id, name, string.Equals(id, defaultId, StringComparison.Ordinal)));
            }

            return endpoints.OrderByDescending(endpoint => endpoint.IsDefault)
                .ThenBy(endpoint => endpoint.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        finally
        {
            for (var index = 0; index < devices.Count; index++) devices[index].Dispose();
        }
    }

    public async Task PlayTestSoundAsync(
        string endpointId,
        AudioTestSound sound,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        if (endpointId.Length > MaximumEndpointIdLength ||
            !GetActiveOutputs().Any(endpoint => string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Вибраний аудіовихід недоступний.");
        }

        int[] frequencies = sound switch
        {
            AudioTestSound.Tone => [880],
            AudioTestSound.Chime => [880, 1175, 1397],
            _ => throw new ArgumentOutOfRangeException(nameof(sound))
        };
        var durationPerNote = sound == AudioTestSound.Tone
            ? TestToneDurationMilliseconds
            : TestNoteDurationMilliseconds;
        using var audioData = new MemoryStream(CreateTestSamples(frequencies, durationPerNote));
        using RawSourceWaveStream source = new(audioData, new WaveFormat(SampleRate, 16, 1));
        using MMDeviceEnumerator enumerator = new();
        using MMDevice device = enumerator.GetDevice(endpointId);
        using WasapiOut output = new(device, AudioClientShareMode.Shared, useEventSync: true, latency: 100);
        var stopped = new TaskCompletionSource<StoppedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPlaybackStopped(object? sender, StoppedEventArgs args) => stopped.TrySetResult(args);
        output.PlaybackStopped += OnPlaybackStopped;
        try
        {
            output.Init(source);
            output.Play();
            StoppedEventArgs result = await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            if (result.Exception is not null) throw result.Exception;
        }
        finally
        {
            output.PlaybackStopped -= OnPlaybackStopped;
            if (output.PlaybackState != PlaybackState.Stopped) output.Stop();
        }
    }

    internal static byte[] CreateTestSamples(IReadOnlyList<int> frequencies, int durationMilliseconds)
    {
        if (frequencies.Count is < 1 or > 3 ||
            frequencies.Any(frequency => frequency is < 200 or > 4_000) ||
            durationMilliseconds is < 100 or > TestToneDurationMilliseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(frequencies));
        }

        var samplesPerNote = SampleRate * durationMilliseconds / 1_000;
        var samples = new short[samplesPerNote * frequencies.Count];
        var fadeSamples = SampleRate * 20 / 1_000;
        for (var noteIndex = 0; noteIndex < frequencies.Count; noteIndex++)
        {
            var frequency = frequencies[noteIndex];
            for (var sampleIndex = 0; sampleIndex < samplesPerNote; sampleIndex++)
            {
                var fadeIn = Math.Min(1d, sampleIndex / (double)fadeSamples);
                var fadeOut = Math.Min(1d, (samplesPerNote - 1 - sampleIndex) / (double)fadeSamples);
                var envelope = Math.Max(0d, Math.Min(fadeIn, fadeOut));
                var value = Math.Sin(2 * Math.PI * frequency * sampleIndex / SampleRate) *
                    MaximumAmplitude * envelope;
                samples[noteIndex * samplesPerNote + sampleIndex] = (short)Math.Round(value * short.MaxValue);
            }
        }

        var bytes = new byte[samples.Length * sizeof(short)];
        Buffer.BlockCopy(samples, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
