using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Contracts.DeviceCommands;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;

namespace Watcher.EdgeAgent.UnitTests.Audio;

public sealed class WasapiAudioPlayerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kgta-wasapi-tests", Guid.NewGuid().ToString("N"));

    public WasapiAudioPlayerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task StartAsync_RejectsExpiredCommandBeforeOpeningAudioDevice()
    {
        await using WasapiAudioPlayer player = CreatePlayer();
        PlaybackRequest request = CreateRequest(Path.Combine(_root, "alarm.wav"),
            DateTimeOffset.UtcNow.AddSeconds(-1), "endpoint");

        AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => player.StartAsync(request, CancellationToken.None));

        Assert.Equal("command_expired", exception.DiagnosticCode);
    }

    [Fact]
    public async Task StartAsync_RequiresConfiguredAudioEndpoint()
    {
        await using WasapiAudioPlayer player = CreatePlayer();
        PlaybackRequest request = CreateRequest(Path.Combine(_root, "alarm.wav"),
            DateTimeOffset.UtcNow.AddMinutes(1), null);

        AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => player.StartAsync(request, CancellationToken.None));

        Assert.Equal("audio_output_unavailable", exception.DiagnosticCode);
    }

    [Fact]
    public async Task StartAsync_RejectsFileOutsideConfiguredAudioRoot()
    {
        var externalFile = Path.Combine(Path.GetTempPath(), $"kgta-audio-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(externalFile, new byte[44]);
        try
        {
            await using WasapiAudioPlayer player = CreatePlayer();
            PlaybackRequest request = CreateRequest(externalFile,
                DateTimeOffset.UtcNow.AddMinutes(1), "endpoint");

            AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
                () => player.StartAsync(request, CancellationToken.None));

            Assert.Equal("preset_missing", exception.DiagnosticCode);
        }
        finally
        {
            File.Delete(externalFile);
        }
    }

    [Fact]
    public async Task StartAsync_RejectsUnsupportedWaveBeforeOpeningAudioDevice()
    {
        var audioFile = Path.Combine(_root, "alarm.wav");
        await File.WriteAllBytesAsync(audioFile, new byte[44]);
        await using WasapiAudioPlayer player = CreatePlayer();

        AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => player.StartAsync(CreateRequest(audioFile, DateTimeOffset.UtcNow.AddMinutes(1), "endpoint"),
                CancellationToken.None));

        Assert.Equal("audio_format_unsupported", exception.DiagnosticCode);
    }

    [Fact]
    public async Task StopAsync_ReturnsFalseWhenTargetIsNotPlaying()
    {
        await using WasapiAudioPlayer player = CreatePlayer();

        Assert.False(await player.StopAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [Fact]
    public async Task ConfirmStartedAsync_RejectsUnknownCommand()
    {
        await using WasapiAudioPlayer player = CreatePlayer();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => player.ConfirmStartedAsync(Guid.NewGuid(), CancellationToken.None));
    }

    private WasapiAudioPlayer CreatePlayer() => new(_root, new NoopPlaybackEventHandler(),
        NullLogger<WasapiAudioPlayer>.Instance);

    private static PlaybackRequest CreateRequest(string path, DateTimeOffset deadlineUtc, string? endpointId) =>
        new(Guid.NewGuid(), "air_raid", path, deadlineUtc, endpointId);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class NoopPlaybackEventHandler : IAudioPlaybackEventHandler
    {
        public Task OnPlaybackFinishedAsync(Guid commandId, DeviceCommandState state,
            string? diagnosticCode, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
