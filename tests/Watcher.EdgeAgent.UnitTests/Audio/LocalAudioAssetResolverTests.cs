using System.Security.Cryptography;
using System.Text;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Audio;

namespace Watcher.EdgeAgent.UnitTests.Audio;

public sealed class LocalAudioAssetResolverTests : IDisposable
{
    private readonly string _root = TestTemporaryPaths.CreateDirectory("kgta-audio-tests");
    private readonly Guid _locationId = Guid.NewGuid();
    private const string PresetCode = "air_raid";

    [Fact]
    public async Task ResolveAsync_ReturnsOnlyVerifiedPcmPackage()
    {
        var packageDirectory = CreatePackage(CreatePcmWave(), PresetCode);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"));
        var expectedManifestHash = Convert.ToHexString(SHA256.HashData(manifest));

        var resolver = new LocalAudioAssetResolver(_root);
        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, expectedManifestHash, CancellationToken.None);

        Assert.NotNull(asset);
        Assert.Equal(PresetCode, asset.PresetCode);
        Assert.Equal(expectedManifestHash, asset.ManifestHash);
        Assert.True(File.Exists(asset.FilePath));
    }

    [Fact]
    public async Task ResolveAsync_ReturnsActualManifestDigestForCoreToCompare()
    {
        CreatePackage(CreatePcmWave(), PresetCode);
        var resolver = new LocalAudioAssetResolver(_root);

        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, new string('0', 64), CancellationToken.None);

        Assert.Null(asset);
    }

    [Theory]
    [InlineData("../other")]
    [InlineData("..\\other")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("\\\\server\\share\\alarm.wav")]
    public async Task ResolveAsync_RejectsUnsafeFileNames(string fileName)
    {
        var packageDirectory = CreatePackage(CreatePcmWave(), PresetCode);
        var audio = await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "alarm.wav"));
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "manifest.json"), MakeManifest(
            _locationId, PresetCode, fileName, Convert.ToHexString(SHA256.HashData(audio))));
        var digest = Convert.ToHexString(SHA256.HashData(
            await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"))));

        var resolver = new LocalAudioAssetResolver(_root);
        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, digest, CancellationToken.None);

        Assert.Null(asset);
    }

    [Fact]
    public async Task ResolveAsync_RejectsTamperedAudio()
    {
        var packageDirectory = CreatePackage(CreatePcmWave(), PresetCode);
        await File.AppendAllTextAsync(Path.Combine(packageDirectory, "alarm.wav"), "tampered");
        var manifest = await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"));
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifest));

        var resolver = new LocalAudioAssetResolver(_root);
        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, manifestHash, CancellationToken.None);

        Assert.Null(asset);
    }

    [Fact]
    public async Task ResolveAsync_RejectsWrongLocationScope()
    {
        CreatePackage(CreatePcmWave(), PresetCode);
        var resolver = new LocalAudioAssetResolver(_root);

        LocalAudioAsset? asset = await resolver.ResolveAsync(Guid.NewGuid(), PresetCode, new string('a', 64), CancellationToken.None);

        Assert.Null(asset);
    }

    [Fact]
    public async Task ResolveAsync_RejectsCompressedWave()
    {
        var compressedWave = CreatePcmWave(formatCode: 3);
        var packageDirectory = CreatePackage(compressedWave, PresetCode);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"));
        var manifestHash = Convert.ToHexString(SHA256.HashData(manifest));

        var resolver = new LocalAudioAssetResolver(_root);
        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, manifestHash, CancellationToken.None);

        Assert.Null(asset);
    }

    [Theory]
    [InlineData("empty-location")]
    [InlineData("unsafe-preset")]
    [InlineData("invalid-manifest-hash")]
    public async Task ResolveAsync_RejectsInvalidRequestValues(string invalidValue)
    {
        CreatePackage(CreatePcmWave(), PresetCode);
        LocalAudioAssetResolver resolver = new(_root);
        Guid locationId = invalidValue == "empty-location" ? Guid.Empty : _locationId;
        var presetCode = invalidValue == "unsafe-preset" ? "../air_raid" : PresetCode;
        var manifestHash = invalidValue == "invalid-manifest-hash" ? "not-a-digest" : new string('a', 64);

        LocalAudioAsset? asset = await resolver.ResolveAsync(
            locationId, presetCode, manifestHash, CancellationToken.None);

        Assert.Null(asset);
    }

    [Fact]
    public async Task ResolveAsync_ReturnsNullWhenLocationPackageIsMissing()
    {
        LocalAudioAssetResolver resolver = new(_root);

        LocalAudioAsset? asset = await resolver.ResolveAsync(
            _locationId, PresetCode, new string('a', 64), CancellationToken.None);

        Assert.Null(asset);
    }

    [Theory]
    [InlineData("array-root")]
    [InlineData("malformed-json")]
    [InlineData("unsupported-version")]
    [InlineData("invalid-location")]
    [InlineData("missing-presets")]
    [InlineData("presets-not-array")]
    [InlineData("preset-not-found")]
    [InlineData("missing-audio-hash")]
    [InlineData("missing-file-name")]
    [InlineData("invalid-audio-hash")]
    [InlineData("duplicate-preset")]
    [InlineData("missing-audio-file")]
    public async Task ResolveAsync_RejectsMalformedOrAmbiguousManifest(string scenario)
    {
        var audio = CreatePcmWave();
        var packageDirectory = CreatePackage(audio, PresetCode);
        var audioHash = Convert.ToHexString(SHA256.HashData(audio));
        var manifest = CreateManifestScenario(scenario, audioHash);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"), manifestBytes);
        var digest = Convert.ToHexString(SHA256.HashData(manifestBytes));
        LocalAudioAssetResolver resolver = new(_root);

        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, digest, CancellationToken.None);

        Assert.Null(asset);
    }

    [Fact]
    public async Task ResolveAsync_RejectsManifestLargerThanConfiguredLimit()
    {
        var packageDirectory = CreatePackage(CreatePcmWave(), PresetCode);
        await File.WriteAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"), new byte[65 * 1024]);
        var manifest = await File.ReadAllBytesAsync(Path.Combine(packageDirectory, "manifest.json"));
        var digest = Convert.ToHexString(SHA256.HashData(manifest));
        LocalAudioAssetResolver resolver = new(_root);

        LocalAudioAsset? asset = await resolver.ResolveAsync(_locationId, PresetCode, digest, CancellationToken.None);

        Assert.Null(asset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreatePackage(byte[] audio, string presetCode)
    {
        var packageDirectory = Path.Combine(_root, _locationId.ToString("D"));
        Directory.CreateDirectory(packageDirectory);
        var audioPath = Path.Combine(packageDirectory, "alarm.wav");
        File.WriteAllBytes(audioPath, audio);
        var fileHash = Convert.ToHexString(SHA256.HashData(audio));
        File.WriteAllText(Path.Combine(packageDirectory, "manifest.json"),
            MakeManifest(_locationId, presetCode, "alarm.wav", fileHash), new UTF8Encoding(false));
        return packageDirectory;
    }

    private static string MakeManifest(Guid locationId, string presetCode, string fileName, string sha256) =>
        $$"""{"schemaVersion":1,"locationId":"{{locationId:D}}","presets":[{"presetCode":"{{presetCode}}","fileName":"{{fileName}}","sha256":"{{sha256}}"}]}""";

    private string CreateManifestScenario(string scenario, string audioHash)
    {
        var validPreset = $$"""{"presetCode":"{{PresetCode}}","fileName":"alarm.wav","sha256":"{{audioHash}}"}""";
        return scenario switch
        {
            "array-root" => "[]",
            "malformed-json" => "{",
            "unsupported-version" => $$"""{"schemaVersion":2,"locationId":"{{_locationId:D}}","presets":[{{validPreset}}]}""",
            "invalid-location" => $$"""{"schemaVersion":1,"locationId":"invalid","presets":[{{validPreset}}]}""",
            "missing-presets" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}"}""",
            "presets-not-array" => "{\"schemaVersion\":1,\"locationId\":\"" + _locationId.ToString("D") + "\",\"presets\":{}}",
            "preset-not-found" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}","presets":[]}""",
            "missing-audio-hash" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}","presets":[{"presetCode":"{{PresetCode}}","fileName":"alarm.wav"}]}""",
            "missing-file-name" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}","presets":[{"presetCode":"{{PresetCode}}","sha256":"{{audioHash}}"}]}""",
            "invalid-audio-hash" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}","presets":[{"presetCode":"{{PresetCode}}","fileName":"alarm.wav","sha256":"invalid"}]}""",
            "duplicate-preset" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}","presets":[{{validPreset}},{{validPreset}}]}""",
            "missing-audio-file" => $$"""{"schemaVersion":1,"locationId":"{{_locationId:D}}","presets":[{"presetCode":"{{PresetCode}}","fileName":"missing.wav","sha256":"{{audioHash}}"}]}""",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static byte[] CreatePcmWave(ushort formatCode = 1)
    {
        const int sampleRate = 8_000;
        const ushort channels = 1;
        const ushort bitsPerSample = 16;
        const ushort blockAlign = channels * (bitsPerSample / 8);
        const int dataLength = sampleRate * blockAlign;
        var wave = new byte[44 + dataLength];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wave, 0);
        BitConverter.GetBytes(wave.Length - 8).CopyTo(wave, 4);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wave, 8);
        BitConverter.GetBytes(16).CopyTo(wave, 16);
        BitConverter.GetBytes(formatCode).CopyTo(wave, 20);
        BitConverter.GetBytes(channels).CopyTo(wave, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wave, 24);
        BitConverter.GetBytes(sampleRate * blockAlign).CopyTo(wave, 28);
        BitConverter.GetBytes(blockAlign).CopyTo(wave, 32);
        BitConverter.GetBytes(bitsPerSample).CopyTo(wave, 34);
        Encoding.ASCII.GetBytes("data").CopyTo(wave, 36);
        BitConverter.GetBytes(dataLength).CopyTo(wave, 40);
        return wave;
    }
}
