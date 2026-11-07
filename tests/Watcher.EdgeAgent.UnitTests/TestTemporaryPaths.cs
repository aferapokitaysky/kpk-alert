namespace Watcher.EdgeAgent.UnitTests;

internal static class TestTemporaryPaths
{
    public static string CreateDirectory(string prefix)
    {
        var temporaryRoot = ResolveLinks(Path.GetTempPath());
        var path = System.IO.Path.Combine(temporaryRoot, prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ResolveLinks(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var current = System.IO.Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("Шлях тестового каталогу не має кореня файлової системи.");

        foreach (var segment in fullPath[current.Length..].Split(
                     [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            FileSystemInfo entry = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);
            if (entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException("Не вдалося визначити ціль тимчасового каталогу.");
            }
        }

        return System.IO.Path.GetFullPath(current);
    }
}
