namespace Watcher.Api.AlertSources;

internal static class BoundedHttpContentReader
{
    public static async Task<byte[]> ReadAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (maximumBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        if (content.Headers.ContentLength is long contentLength && contentLength > maximumBytes)
        {
            throw new ResponseTooLargeException(maximumBytes);
        }

        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream destination = new(Math.Min(maximumBytes, 4096));
        var buffer = new byte[4096];
        while (true)
        {
            var remaining = maximumBytes - (int)destination.Length;
            var bytesRead = await source.ReadAsync(
                buffer.AsMemory(0, Math.Min(buffer.Length, remaining + 1)), cancellationToken);
            if (bytesRead == 0)
            {
                return destination.ToArray();
            }

            if (bytesRead > remaining)
            {
                throw new ResponseTooLargeException(maximumBytes);
            }

            destination.Write(buffer, 0, bytesRead);
        }
    }

    internal sealed class ResponseTooLargeException(int maximumBytes)
        : Exception($"The response exceeded the {maximumBytes}-byte limit.");
}
