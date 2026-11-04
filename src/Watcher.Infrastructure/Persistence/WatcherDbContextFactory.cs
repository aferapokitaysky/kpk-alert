using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Watcher.Infrastructure.Persistence;

public sealed class WatcherDbContextFactory : IDesignTimeDbContextFactory<WatcherDbContext>
{
    public WatcherDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Watcher")
            ?? "Host=localhost;Database=kgta_alert;Username=kgta_alert;Password=design-time-only";
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new WatcherDbContext(options);
    }
}
