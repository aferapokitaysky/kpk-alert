using Microsoft.EntityFrameworkCore;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class RoleCatalogMigrationTests(PostgresApiFixture fixture) : IClassFixture<PostgresApiFixture>
{
    [PostgresFact]
    public async Task MigrationSeedsSystemRolesWithoutAssignments()
    {
        using WatcherDbContext db = fixture.CreateDbContext();

        Dictionary<string, string> roles = await db.Roles
            .AsNoTracking()
            .ToDictionaryAsync(role => role.Code, role => role.Name);

        Assert.Equal(4, roles.Count);
        Assert.Equal("Адміністратор", roles["admin"]);
        Assert.Equal("Аудитор", roles["auditor"]);
        Assert.Equal("Оператор оповіщення", roles["notificator"]);
        Assert.Equal("Фахівець зі звуку", roles["sound_engineer"]);
        Assert.Empty(await db.UserRoles.AsNoTracking().ToListAsync());
    }
}
