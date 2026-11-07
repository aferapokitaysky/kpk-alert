using System.Buffers.Binary;

namespace Watcher.Domain.Audio;

public static class PcmWaveFileValidator
{
    private const int MinimumSampleRate = 8_000;
    private const int MaximumSampleRate = 192_000;
    private const int MaximumDurationSeconds = 900;

    public static bool IsSupported(Stream stream, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (!stream.CanRead || !stream.CanSeek || stream.Length < 44)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[12];
        if (!ReadExactly(stream, header) ||
            !header[..4].SequenceEqual("RIFF"u8) ||
            !header[8..12].SequenceEqual("WAVE"u8))
        {
            return false;
        }

        var riffEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
        if (riffEnd > stream.Length || riffEnd < 12)
        {
            return false;
        }

        ushort channels = 0;
        ushort bitsPerSample = 0;
        ushort blockAlign = 0;
        uint sampleRate = 0;
        long dataLength = -1;
        var hasFormat = false;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> format = stackalloc byte[16];
        while (stream.Position + chunkHeader.Length <= riffEnd)
        {
            if (!ReadExactly(stream, chunkHeader))
            {
                return false;
            }

            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);
            var chunkStart = stream.Position;
            var chunkEnd = chunkStart + chunkLength;
            var paddedChunkEnd = chunkEnd + (chunkLength & 1);
            if (chunkEnd > riffEnd || paddedChunkEnd > riffEnd)
            {
                return false;
            }

            if (chunkHeader[..4].SequenceEqual("fmt "u8))
            {
                if (hasFormat || chunkLength < 16)
                {
                    return false;
                }

                if (!ReadExactly(stream, format))
                {
                    return false;
                }

                var encoding = BinaryPrimitives.ReadUInt16LittleEndian(format[..2]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format[2..4]);
                sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(format[4..8]);
                var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(format[8..12]);
                blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(format[12..14]);
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(format[14..16]);

                if (encoding != 1 || channels is < 1 or > 2 ||
                    sampleRate is < MinimumSampleRate or > MaximumSampleRate ||
                    bitsPerSample is not (8 or 16 or 24 or 32))
                {
                    return false;
                }

                var expectedBlockAlign = channels * (bitsPerSample / 8);
                if (blockAlign != expectedBlockAlign || byteRate != sampleRate * blockAlign)
                {
                    return false;
                }

                hasFormat = true;
            }
            else if (chunkHeader[..4].SequenceEqual("data"u8))
            {
                if (dataLength >= 0)
                {
                    return false;
                }

                dataLength = chunkLength;
            }

            stream.Position = paddedChunkEnd;
        }

        if (!hasFormat || dataLength <= 0 || blockAlign == 0 || dataLength % blockAlign != 0)
        {
            return false;
        }

        var seconds = dataLength / (double)(sampleRate * blockAlign);
        if (seconds > MaximumDurationSeconds)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private static bool ReadExactly(Stream stream, Span<byte> buffer)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = stream.Read(buffer[totalRead..]);
            if (bytesRead == 0)
            {
                return false;
            }

            totalRead += bytesRead;
        }

        return true;
    }
}
