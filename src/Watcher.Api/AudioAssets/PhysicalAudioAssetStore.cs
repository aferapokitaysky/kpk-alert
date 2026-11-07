using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Watcher.Domain.Audio;

namespace Watcher.Api.AudioAssets;

public sealed record StoredAudioAsset(string Sha256, long SizeBytes, int DurationSeconds);

public sealed class InvalidAudioAssetException(string message) : Exception(message);

public sealed class PhysicalAudioAssetStore
{
    public const long MaximumFileBytes = 100L * 1024 * 1024;
    private readonly string _storageRoot;

    public PhysicalAudioAssetStore(IOptions<AudioAssetStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.Value.StoragePath))
        {
            throw new InvalidOperationException("Потрібно налаштувати шлях до сховища аудіопакетів.");
        }

        _storageRoot = Path.GetFullPath(options.Value.StoragePath);
    }

    public async Task<StoredAudioAsset> SaveAsync(
        Guid locationId,
        Guid assetId,
        Stream source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var directory = GetLocationDirectory(locationId);
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{assetId:N}.{Guid.NewGuid():N}.tmp");
        var destinationPath = GetAssetPath(locationId, assetId);

        try
        {
            await using FileStream destination = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long size = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                size = checked(size + read);
                if (size > MaximumFileBytes)
                {
                    throw new InvalidAudioAssetException("Розмір WAV-файла перевищує 100 MiB.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            if (size == 0)
            {
                throw new InvalidAudioAssetException("WAV-файл порожній.");
            }

            destination.Close();
            int durationSeconds;
            await using (FileStream validationStream = new(
                             temporaryPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             4096,
                             FileOptions.SequentialScan))
            {
                if (!PcmWaveFileValidator.IsSupported(validationStream, out TimeSpan duration))
                {
                    throw new InvalidAudioAssetException("Файл має бути коректним PCM WAV тривалістю до 15 хвилин.");
                }

                durationSeconds = checked((int)Math.Ceiling(duration.TotalSeconds));
            }

            File.Move(temporaryPath, destinationPath);
            return new StoredAudioAsset(Convert.ToHexString(hash.GetHashAndReset()), size, durationSeconds);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    public FileStream? OpenRead(Guid locationId, Guid assetId)
    {
        var path = GetAssetPath(locationId, assetId);
        if (!File.Exists(path))
        {
            return null;
        }

        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    public Task DeleteAsync(Guid locationId, Guid assetId)
    {
        File.Delete(GetAssetPath(locationId, assetId));
        return Task.CompletedTask;
    }

    private string GetAssetPath(Guid locationId, Guid assetId) =>
        Path.Combine(GetLocationDirectory(locationId), $"{assetId:N}.wav");

    private string GetLocationDirectory(Guid locationId)
    {
        if (locationId == Guid.Empty)
        {
            throw new ArgumentException("Ідентифікатор локації не може бути порожнім.", nameof(locationId));
        }

        return Path.Combine(_storageRoot, locationId.ToString("N"));
    }
}
