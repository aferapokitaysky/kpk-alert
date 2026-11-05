using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class RoleScopeMigrationRollbackTests : IClassFixture<PostgresApiFixture>
{
    private const string MigrationUnderTest = "20261104211613_AddCommandIdempotencyAndRoleScope";
    private const string PreviousMigration = "20261104210917_AddDevicesAndSessions";
    private readonly PostgresApiFixture _fixture;

    public RoleScopeMigrationRollbackTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Downgrade_refuses_global_roles_before_changing_the_schema()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        using WatcherDbContext db = _fixture.CreateDbContext();
        await db.HideNewerMigrationsFromHistoryAsync();
        IMigrator migrator = db.Database.GetService<IMigrator>();

        InvalidOperationException migrationException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => migrator.MigrateAsync(PreviousMigration));
        PostgresException exception = Assert.IsType<PostgresException>(migrationException.InnerException);

        Assert.Equal("55000", exception.SqlState);
        Assert.Contains("глобальні призначення ролей", exception.MessageText, StringComparison.Ordinal);
        Assert.Contains(
            MigrationUnderTest,
            await db.Database.GetAppliedMigrationsAsync());
        Assert.Null(await db.UserRoles.Where(role => role.UserId == seed.AdministratorId)
            .Select(role => role.LocationId)
            .SingleAsync());
    }
}

public sealed class RoleScopeMigrationSuccessTests : IClassFixture<PostgresApiFixture>
{
    private const string MigrationUnderTest = "20261104211613_AddCommandIdempotencyAndRoleScope";
    private const string PreviousMigration = "20261104210917_AddDevicesAndSessions";
    private readonly PostgresApiFixture _fixture;

    public RoleScopeMigrationSuccessTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Downgrade_restores_required_location_without_a_default()
    {
        await _fixture.ResetAndSeedAsync();
        using WatcherDbContext db = _fixture.CreateDbContext();
        await db.HideNewerMigrationsFromHistoryAsync();
        IMigrator migrator = db.Database.GetService<IMigrator>();

        await migrator.MigrateAsync(PreviousMigration);

        await using NpgsqlCommand command = new(
            "SELECT is_nullable, column_default FROM information_schema.columns " +
            "WHERE table_schema = current_schema() AND table_name = 'user_roles' AND column_name = 'LocationId'",
            (NpgsqlConnection)db.Database.GetDbConnection());
        await db.Database.OpenConnectionAsync();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("NO", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
    }
}

internal static class RoleScopeMigrationTestExtensions
{
    public static Task<int> HideNewerMigrationsFromHistoryAsync(this WatcherDbContext db) =>
        db.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" > {0}",
            "20261104211613_AddCommandIdempotencyAndRoleScope");
}
