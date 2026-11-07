using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Watcher.Domain.Audio;
using Watcher.EdgeAgent.Core;

namespace Watcher.EdgeAgent.Infrastructure.Audio;

/// <summary>
/// Reads pre-provisioned local audio packages. The manifest contract is deliberately
/// strict because the server-side package endpoint is not part of the current API yet.
/// </summary>
public sealed class LocalAudioAssetResolver(string audioRootDirectory) : IAudioAssetResolver
{
    private const int MaximumManifestBytes = 64 * 1024;
    private const long MaximumAudioFileBytes = 100 * 1024 * 1024;
    private const int MaximumPlaybackSeconds = 900;
    private static readonly Regex SafePresetCode = new("^[a-z0-9_]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex SafeFileName = new("^[a-zA-Z0-9._-]{1,120}\\.wav$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly string _root = Path.GetFullPath(audioRootDirectory);

    public async Task<LocalAudioAsset?> ResolveAsync(
        Guid locationId,
        string presetCode,
        string manifestHash,
        CancellationToken cancellationToken)
    {
        if (locationId == Guid.Empty || !SafePresetCode.IsMatch(presetCode) || !IsSha256(manifestHash))
        {
            return null;
        }

        var locationDirectory = Path.Combine(_root, locationId.ToString("D"));
        var manifestPath = Path.Combine(locationDirectory, "manifest.json");
        if (!IsSafeExistingPath(locationDirectory, manifestPath))
        {
            return null;
        }

        try
        {
            await using FileStream? manifestStream = OpenSafeReadStream(locationDirectory, manifestPath);
            if (manifestStream is null || manifestStream.Length is <= 0 or > MaximumManifestBytes)
            {
                return null;
            }

            var manifestLength = manifestStream.Length;
            var manifestBytes = await ReadBoundedAsync(manifestStream, MaximumManifestBytes, cancellationToken);
            if (manifestBytes is null || manifestBytes.Length != manifestLength ||
                !IsSafeExistingPath(locationDirectory, manifestPath))
            {
                return null;
            }

            var actualManifestHash = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualManifestHash), Convert.FromHexString(manifestHash)))
            {
                return null;
            }

            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });

            if (!TryReadManifest(document.RootElement, locationId, presetCode, out var fileName, out var sha256))
            {
                return null;
            }

            var audioPath = Path.Combine(locationDirectory, fileName!);
            if (!IsSafeExistingPath(locationDirectory, audioPath))
            {
                return null;
            }

            await using FileStream? audioStream = OpenSafeReadStream(locationDirectory, audioPath);
            if (audioStream is null || audioStream.Length is <= 0 or > MaximumAudioFileBytes)
            {
                return null;
            }

            var audioLength = audioStream.Length;
            var actualFileHashValue = await HashBoundedAsync(audioStream, MaximumAudioFileBytes, cancellationToken);
            if (actualFileHashValue is null || audioStream.Length != audioLength ||
                !IsSafeExistingPath(locationDirectory, audioPath))
            {
                return null;
            }

            var actualFileHash = actualFileHashValue;
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualFileHash), Convert.FromHexString(sha256!)))
            {
                return null;
            }

            audioStream.Position = 0;
            if (!PcmWaveFileValidator.IsSupported(audioStream, out _))
            {
                return null;
            }

            // The digest covers the exact manifest bytes. The eventual device package API
            // must publish those same bytes (or define a canonicalization algorithm).
            return new LocalAudioAsset(presetCode, actualManifestHash, audioPath, actualFileHash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or
                                           ArgumentException or NotSupportedException or FormatException)
        {
            return null;
        }
    }

    private FileStream? OpenSafeReadStream(string allowedDirectory, string path)
    {
        if (!IsSafeExistingPath(allowedDirectory, path))
        {
            return null;
        }

        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (!IsSafeExistingPath(allowedDirectory, path))
        {
            stream.Dispose();
            return null;
        }

        return stream;
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream result = new((int)Math.Min(stream.Length, maximumBytes));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (result.Length + read > maximumBytes)
            {
                return null;
            }

            result.Write(buffer, 0, read);
        }

        return result.ToArray();
    }

    private static async Task<string?> HashBoundedAsync(
        Stream stream,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[64 * 1024];
        long totalRead = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > maximumBytes)
            {
                return null;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private bool IsSafeExistingPath(string allowedDirectory, string candidatePath)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(_root);
        var fullAllowedDirectory = Path.GetFullPath(allowedDirectory);
        var fullCandidate = Path.GetFullPath(candidatePath);
        if (IsUnc(fullRoot) || IsUnc(fullCandidate) ||
            !IsWithinRoot(fullRoot, fullAllowedDirectory) || !IsWithinRoot(fullRoot, fullCandidate) ||
            !File.Exists(fullCandidate) && !Directory.Exists(fullCandidate))
        {
            return false;
        }

        var current = Path.GetPathRoot(fullCandidate)!;
        var relativeFromVolumeRoot = Path.GetRelativePath(current, fullCandidate);
        if (HasReparsePoint(current))
        {
            return false;
        }

        foreach (var segment in relativeFromVolumeRoot.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     .Where(segment => segment.Length > 0 && segment != "."))
        {
            current = Path.Combine(current, segment);
            if (HasReparsePoint(current))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsWithinRoot(string root, string path)
    {
        var prefix = root + Path.DirectorySeparatorChar;
        return string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
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

    private static bool IsUnc(string path) => path.StartsWith("\\\\", StringComparison.Ordinal) ||
                                               path.StartsWith("//", StringComparison.Ordinal);

    private static bool TryReadManifest(
        JsonElement root,
        Guid expectedLocationId,
        string expectedPresetCode,
        out string? fileName,
        out string? sha256)
    {
        fileName = null;
        sha256 = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("schemaVersion", out JsonElement schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number || !schemaVersion.TryGetInt32(out var version) || version != 1 ||
            !root.TryGetProperty("locationId", out JsonElement location) || location.ValueKind != JsonValueKind.String ||
            !Guid.TryParse(location.GetString(), out Guid locationId) || locationId != expectedLocationId ||
            !root.TryGetProperty("presets", out JsonElement presets) || presets.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var matches = 0;
        foreach (JsonElement preset in presets.EnumerateArray())
        {
            if (preset.ValueKind != JsonValueKind.Object ||
                !preset.TryGetProperty("presetCode", out JsonElement code) || code.ValueKind != JsonValueKind.String ||
                !string.Equals(code.GetString(), expectedPresetCode, StringComparison.Ordinal))
            {
                continue;
            }

            matches++;
            if (!preset.TryGetProperty("fileName", out JsonElement file) || file.ValueKind != JsonValueKind.String ||
                !preset.TryGetProperty("sha256", out JsonElement hash) || hash.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            fileName = file.GetString();
            sha256 = hash.GetString();
        }

        return matches == 1 && fileName is not null && SafeFileName.IsMatch(fileName) &&
               IsSha256(sha256);
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(character => Uri.IsHexDigit(character));
}
