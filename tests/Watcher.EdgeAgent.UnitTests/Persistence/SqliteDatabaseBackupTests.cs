using Microsoft.Data.Sqlite;
using Watcher.EdgeAgent.Infrastructure.Persistence;

namespace Watcher.EdgeAgent.UnitTests.Persistence;

public sealed class SqliteDatabaseBackupTests
{
    [Fact]
    public void Create_CopiesCommittedWalDataWithoutIncludingAnOpenTransaction()
    {
        string directory = CreateTemporaryDirectory();
        string sourcePath = Path.Combine(directory, "edge-agent.db");
        string destinationPath = Path.Combine(directory, "backups", "edge-agent.db");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);

        try
        {
            using (SqliteConnection source = Open(sourcePath))
            {
                Execute(source, "PRAGMA journal_mode = WAL; PRAGMA wal_autocheckpoint = 0; CREATE TABLE records (value TEXT NOT NULL);");
                Execute(source, "INSERT INTO records VALUES ('committed');");
                using SqliteTransaction activeTransaction = source.BeginTransaction();
                using SqliteCommand uncommittedInsert = source.CreateCommand();
                uncommittedInsert.Transaction = activeTransaction;
                uncommittedInsert.CommandText = "INSERT INTO records VALUES ('uncommitted');";
                uncommittedInsert.ExecuteNonQuery();

                Assert.True(File.Exists(sourcePath + "-wal"));
                SqliteDatabaseBackup.Create(sourcePath, destinationPath);
            }

            using SqliteConnection backup = Open(destinationPath);
            using SqliteCommand query = backup.CreateCommand();
            query.CommandText = "SELECT value FROM records ORDER BY rowid;";
            using SqliteDataReader reader = query.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("committed", reader.GetString(0));
            Assert.False(reader.Read());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Create_DoesNotOverwriteExistingDestination()
    {
        string directory = CreateTemporaryDirectory();
        string sourcePath = Path.Combine(directory, "edge-agent.db");
        string destinationPath = Path.Combine(directory, "backup.db");

        try
        {
            using (SqliteConnection source = Open(sourcePath))
            {
                Execute(source, "CREATE TABLE records (value TEXT NOT NULL);");
            }
            File.WriteAllText(destinationPath, "preserve this file");

            Assert.Throws<IOException>(() => SqliteDatabaseBackup.Create(sourcePath, destinationPath));
            Assert.Equal("preserve this file", File.ReadAllText(destinationPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "kgta-sqlite-backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
