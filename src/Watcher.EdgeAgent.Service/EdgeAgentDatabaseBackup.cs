using System.Security.Cryptography;
using System.Text;
using Watcher.EdgeAgent.Core;
using Watcher.EdgeAgent.Infrastructure.Persistence;

namespace Watcher.EdgeAgent.Service;

internal sealed class EdgeAgentDatabaseBackup(EdgeAgentOptions options, string? stagingDirectory = null)
{
    private readonly string _stagingDirectory = stagingDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "KGTA", "Alert", "backup-staging");

    public EdgeAgentDatabaseBackupResult Create()
    {
        var databasePath = Path.GetFullPath(options.DatabasePath);
        EnsureDirectoryExistsWithoutReparsePoints(_stagingDirectory);

        var backupId = Guid.NewGuid();
        var backupPath = GetBackupPath(backupId);
        SqliteDatabaseBackup.Create(databasePath, backupPath);

        return new EdgeAgentDatabaseBackupResult(backupId, CreateDatabasePathHash(databasePath));
    }

    private string GetBackupPath(Guid backupId) => Path.Combine(
        Path.GetFullPath(_stagingDirectory), $"edge-agent-{backupId:N}.db");

    internal static string CreateDatabasePathHash(string databasePath)
    {
        var canonicalPath = Path.GetFullPath(databasePath).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalPath)));
    }

    private static void EnsureDirectoryExistsWithoutReparsePoints(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            throw new DirectoryNotFoundException("Каталог підготовки резервної копії не знайдено.");
        }

        for (DirectoryInfo? current = directory; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("Шлях резервної копії містить reparse point.");
            }
        }
    }
}

internal sealed record EdgeAgentDatabaseBackupResult(Guid BackupId, string DatabasePathHash);
