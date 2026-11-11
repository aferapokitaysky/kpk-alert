using System.Buffers.Binary;
using Watcher.EdgeAgent.Infrastructure.Audio;

namespace Watcher.EdgeAgent.UnitTests.Audio;

public sealed class WasapiAudioEndpointControlTests
{
    [Theory]
    [InlineData(1, 450, 21_600)]
    [InlineData(3, 220, 31_680)]
    public void CreateTestSamples_ReturnsBoundedLowAmplitudePcm(int noteCount, int durationMilliseconds, int expectedBytes)
    {
        var frequencies = Enumerable.Range(0, noteCount).Select(index => 880 + (index * 200)).ToArray();

        var samples = WasapiAudioEndpointControl.CreateTestSamples(frequencies, durationMilliseconds);

        Assert.Equal(expectedBytes, samples.Length);
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(0, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(samples.Length - 2, 2)));
        var maximumAmplitude = Enumerable.Range(0, samples.Length / 2)
            .Select(index => Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(samples.AsSpan(index * 2, 2))))
            .Max();
        Assert.InRange(maximumAmplitude, 1, 4_000);
    }

    [Theory]
    [InlineData(0, 220)]
    [InlineData(4, 220)]
    [InlineData(1, 451)]
    public void CreateTestSamples_RejectsUnboundedInput(int noteCount, int durationMilliseconds)
    {
        var frequencies = Enumerable.Repeat(880, noteCount).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WasapiAudioEndpointControl.CreateTestSamples(frequencies, durationMilliseconds));
    }
}
