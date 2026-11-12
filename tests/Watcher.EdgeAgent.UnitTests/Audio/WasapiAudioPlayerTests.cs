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
    public async Task StartAsync_RejectsFileWhoseHashChangedAfterManifestValidation()
    {
        var audioFile = Path.Combine(_root, "alarm.wav");
        await File.WriteAllBytesAsync(audioFile, CreatePcmWave());
        await using WasapiAudioPlayer player = CreatePlayer();

        AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => player.StartAsync(CreateRequest(audioFile, DateTimeOffset.UtcNow.AddMinutes(1), "endpoint"),
                CancellationToken.None));

        Assert.Equal("manifest_mismatch", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ReadVerifiedAudioAsync_ReturnsSameBytesThatWereHashedEvenIfPathChangesLater()
    {
        var audioFile = Path.Combine(_root, "alarm.wav");
        var originalAudio = CreatePcmWave();
        await File.WriteAllBytesAsync(audioFile, originalAudio);
        byte[] verifiedAudio;
        await using (FileStream stream = new(audioFile, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            verifiedAudio = await WasapiAudioPlayer.ReadVerifiedAudioAsync(
                stream, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(originalAudio)),
                CancellationToken.None);
        }

        byte[] replacedAudio = [.. originalAudio];
        replacedAudio[^1] ^= 0x7f;
        await File.WriteAllBytesAsync(audioFile, replacedAudio);

        Assert.Equal(originalAudio, verifiedAudio);
        Assert.NotEqual(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(replacedAudio)),
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(verifiedAudio)));
    }

    [Fact]
    public async Task ReadVerifiedAudioAsync_RejectsMalformedHash()
    {
        var audioFile = Path.Combine(_root, "alarm.wav");
        await File.WriteAllBytesAsync(audioFile, CreatePcmWave());
        await using FileStream stream = new(audioFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => WasapiAudioPlayer.ReadVerifiedAudioAsync(stream, "not-a-hash", CancellationToken.None));

        Assert.Equal("manifest_mismatch", exception.DiagnosticCode);
    }

    [Fact]
    public async Task ReadVerifiedAudioAsync_RejectsEmptyAudioFile()
    {
        var audioFile = Path.Combine(_root, "empty.wav");
        await File.WriteAllBytesAsync(audioFile, []);
        await using FileStream stream = new(audioFile, FileMode.Open, FileAccess.Read, FileShare.Read);

        AudioPlaybackException exception = await Assert.ThrowsAsync<AudioPlaybackException>(
            () => WasapiAudioPlayer.ReadVerifiedAudioAsync(stream, new string('0', 64), CancellationToken.None));

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
        new(Guid.NewGuid(), "air_raid", path, new string('0', 64), deadlineUtc, endpointId);

    private static byte[] CreatePcmWave()
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        const int sampleRate = 24_000;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int sampleBytes = 2;
        writer.Write("RIFF"u8);
        writer.Write(38);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * sampleBytes);
        writer.Write((short)(channels * sampleBytes));
        writer.Write(bitsPerSample);
        writer.Write("data"u8);
        writer.Write(sampleBytes);
        writer.Write((short)1);
        return stream.ToArray();
    }

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
