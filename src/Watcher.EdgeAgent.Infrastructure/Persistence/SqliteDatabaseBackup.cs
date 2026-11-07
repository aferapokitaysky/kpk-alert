using Microsoft.Data.Sqlite;

namespace Watcher.EdgeAgent.Infrastructure.Persistence;

public static class SqliteDatabaseBackup
{
    public static void Create(string sourcePath, string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        if (string.Equals(source, destination, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
        {
            throw new ArgumentException("Шлях резервної копії має відрізнятися від шляху бази даних.", nameof(destinationPath));
        }
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Файл локальної бази даних не знайдено.", source);
        }
        if (!Directory.Exists(Path.GetDirectoryName(destination)))
        {
            throw new DirectoryNotFoundException("Каталог резервних копій не знайдено.");
        }

        using (new FileStream(destination, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
        }

        try
        {
            using SqliteConnection sourceConnection = new(new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadOnly,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            sourceConnection.Open();

            using SqliteConnection destinationConnection = new(new SqliteConnectionStringBuilder
            {
                DataSource = destination,
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Private,
                Pooling = false
            }.ToString());
            destinationConnection.Open();
            sourceConnection.BackupDatabase(destinationConnection);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }
}
