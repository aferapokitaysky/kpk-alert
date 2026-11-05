using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class WatcherDbContextModelTests
{
    [Fact]
    public void Model_contains_expected_tables_and_unique_business_keys()
    {
        using WatcherDbContext db = CreateContext();

        var entityNames = db.Model.GetEntityTypes()
            .Select(entity => entity.GetTableName())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("locations", entityNames);
        Assert.Contains("users", entityNames);
        Assert.Contains("roles", entityNames);
        Assert.Contains("devices", entityNames);
        Assert.Contains("alert_commands", entityNames);
        Assert.Contains("minute_of_silence_occurrences", entityNames);
        Assert.Contains("command_deliveries", entityNames);
        Assert.Contains("audit_events", entityNames);
        Assert.Contains("outbox_messages", entityNames);
        Assert.Contains("location_command_sequences", entityNames);

        IIndex codeIndex = db.Model.FindEntityType("Watcher.Domain.Entities.Location")!
            .GetIndexes()
            .Single(index => index.Properties.Select(property => property.Name).SequenceEqual(["Code"]));
        Assert.True(codeIndex.IsUnique);
    }

    [Fact]
    public void Model_enforces_one_target_per_command_and_location()
    {
        using WatcherDbContext db = CreateContext();
        IIndex index = db.Model.FindEntityType("Watcher.Domain.Entities.CommandTarget")!
            .GetIndexes()
            .Single(item => item.Properties.Select(property => property.Name)
                .SequenceEqual(["CommandId", "LocationId"]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Command_deliveries_require_expiry_and_are_unique_per_command_and_device()
    {
        using WatcherDbContext db = CreateContext();
        IEntityType entity = db.Model.FindEntityType(typeof(CommandDelivery))!;
        IIndex uniqueIndex = entity.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(CommandDelivery.CommandId), nameof(CommandDelivery.DeviceId)]));

        Assert.True(uniqueIndex.IsUnique);
        Assert.False(entity.FindProperty(nameof(CommandDelivery.ExpiresAtUtc))!.IsNullable);
        Assert.Equal("character varying(20)",
            entity.FindProperty(nameof(CommandDelivery.State))!.GetColumnType());
    }

    [Fact]
    public void Model_enforces_unique_positive_sequences_per_location()
    {
        using WatcherDbContext db = CreateContext();
        IEntityType entity = db.Model.FindEntityType("Watcher.Domain.Entities.CommandTarget")!;
        IIndex index = entity.GetIndexes().Single(item => item.Properties.Select(property => property.Name)
            .SequenceEqual(["LocationId", "Sequence"]));

        Assert.True(index.IsUnique);
        IEntityType designEntity = db.GetService<IDesignTimeModel>().Model
            .FindEntityType("Watcher.Domain.Entities.CommandTarget")!;
        Assert.Contains(designEntity.GetCheckConstraints(), constraint =>
            constraint.Name == "CK_command_targets_sequence_positive");
    }

    [Fact]
    public void Audit_payload_uses_postgresql_jsonb()
    {
        using WatcherDbContext db = CreateContext();
        IProperty? payload = db.Model.FindEntityType("Watcher.Domain.Entities.AuditEvent")!
            .FindProperty("Payload");

        Assert.Equal("jsonb", payload!.GetColumnType());
    }

    [Fact]
    public void Outbox_payload_uses_jsonb_and_pending_events_have_an_ordered_lookup_index()
    {
        using WatcherDbContext db = CreateContext();
        IEntityType entity = db.Model.FindEntityType("Watcher.Domain.Entities.OutboxMessage")!;
        IProperty payload = entity.FindProperty("Payload")!;
        IIndex pendingIndex = entity.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual(["ProcessedAtUtc", "CreatedAtUtc"]));
        IIndex commandIndex = entity.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual(["CommandId"]));

        Assert.Equal("jsonb", payload.GetColumnType());
        Assert.False(pendingIndex.IsUnique);
        Assert.True(commandIndex.IsUnique);
        Assert.Contains(entity.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(Watcher.Domain.Entities.AlertCommand));
    }

    [Fact]
    public void Migrations_are_ordered_and_each_changes_the_schema()
    {
        using WatcherDbContext db = CreateRelationalContext();
        IMigrationsAssembly assembly = db.GetService<IMigrationsAssembly>();
        IReadOnlyList<string> migrationIds = assembly.Migrations.Keys.ToArray();

        Assert.Equal(9, migrationIds.Count);
        Assert.Equal(migrationIds.Order(StringComparer.Ordinal), migrationIds);
        Assert.All(assembly.Migrations.Values, migrationType =>
        {
            Migration migration = assembly.CreateMigration(migrationType, db.Database.ProviderName!);
            Assert.NotEmpty(migration.UpOperations);
        });
    }

    private static WatcherDbContext CreateContext()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only;Password=model_only")
            .Options;
        return new WatcherDbContext(options);
    }

    private static WatcherDbContext CreateRelationalContext()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only;Password=model_only")
            .Options;
        return new WatcherDbContext(options);
    }
}
