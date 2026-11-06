using System.Data;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Commands;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class PostgresLocationCommandSequenceAllocatorTests
{
    [Fact]
    public async Task Allocate_requires_an_active_command_transaction_before_opening_connection()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql("Host=localhost;Database=allocator_test;Username=allocator_test;Password=unused")
            .Options;
        await using WatcherDbContext db = new(options);
        PostgresLocationCommandSequenceAllocator allocator = new(db);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.AllocateAsync(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("Порядковий номер можна видати лише в транзакції команди.", exception.Message);
        Assert.Equal(ConnectionState.Closed, db.Database.GetDbConnection().State);
    }
}
