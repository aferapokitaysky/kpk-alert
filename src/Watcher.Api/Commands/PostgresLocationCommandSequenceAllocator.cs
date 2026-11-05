using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Commands;

public sealed class PostgresLocationCommandSequenceAllocator(WatcherDbContext db)
    : ILocationCommandSequenceAllocator
{
    public async Task<long> AllocateAsync(Guid locationId, CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = db.Database.CurrentTransaction ??
            throw new InvalidOperationException("Порядковий номер можна видати лише в транзакції команди.");

        DbConnection connection = db.Database.GetDbConnection();
        var openedConnection = connection.State != System.Data.ConnectionState.Open;
        if (openedConnection)
        {
            await db.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            await using DbCommand command = connection.CreateCommand();
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = """
                INSERT INTO location_command_sequences ("LocationId", "NextSequence")
                VALUES (@locationId, 2)
                ON CONFLICT ("LocationId") DO UPDATE
                SET "NextSequence" = location_command_sequences."NextSequence" + 1
                RETURNING "NextSequence" - 1;
                """;
            DbParameter parameter = command.CreateParameter();
            parameter.ParameterName = "locationId";
            parameter.Value = locationId;
            command.Parameters.Add(parameter);

            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is long sequence && sequence > 0
                ? sequence
                : throw new InvalidOperationException("Не вдалося видати порядковий номер корпусу.");
        }
        finally
        {
            if (openedConnection)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
