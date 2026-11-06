using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class DomainInfrastructureInvariantTests
{
    [Fact]
    public void Webhook_operation_model_limits_each_integration_to_one_unresolved_operation()
    {
        using WatcherDbContext db = CreateContext();
        IEntityType operation = db.Model.FindEntityType(typeof(UkraineAlarmWebhookOperation))!;
        IIndex activeOperationIndex = operation.GetIndexes().Single(index =>
            index.Properties.Select(property => property.Name).SequenceEqual([nameof(UkraineAlarmWebhookOperation.IntegrationCode)]));
        IEntityType designOperation = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(UkraineAlarmWebhookOperation))!;
        Assert.True(activeOperationIndex.IsUnique);
        Assert.Equal("\"State\" IN ('Pending', 'Running', 'Unknown')", activeOperationIndex.GetFilter());
        Assert.Contains(designOperation.GetCheckConstraints(), constraint =>
            constraint.Name == "CK_ukrainealarm_webhook_operations_action" &&
            constraint.Sql == "\"Action\" IN ('register', 'update')");
        Assert.Contains(designOperation.GetCheckConstraints(), constraint =>
            constraint.Name == "CK_ukrainealarm_webhook_operations_state" &&
            constraint.Sql == "\"State\" IN ('Pending', 'Running', 'Succeeded', 'Rejected', 'Unknown', 'Resolved')");
        Assert.Contains(operation.GetForeignKeys(), foreignKey =>
            foreignKey.PrincipalEntityType.ClrType == typeof(ExternalIntegrationState) &&
            foreignKey.DeleteBehavior == DeleteBehavior.Restrict);
    }

    [Fact]
    public void Stop_command_model_preserves_zero_duration_despite_database_default()
    {
        using WatcherDbContext db = CreateContext();
        IEntityType command = db.Model.FindEntityType(typeof(AlertCommand))!;
        IProperty duration = command.FindProperty(nameof(AlertCommand.MaximumPlaybackDurationSeconds))!;
        IEntityType designCommand = db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(AlertCommand))!;
        ICheckConstraint payloadConstraint = Assert.Single(designCommand.GetCheckConstraints(),
            item => item.Name == "CK_alert_commands_device_payload");

        Assert.Equal(AlertCommand.DefaultMaximumPlaybackDurationSeconds, duration.GetDefaultValue());
        Assert.Equal(-1, duration.Sentinel);
        Assert.Contains("\"MaximumPlaybackDurationSeconds\" = 0", payloadConstraint.Sql, StringComparison.Ordinal);
        Assert.Contains("\"MaximumPlaybackDurationSeconds\" BETWEEN 1 AND 900", payloadConstraint.Sql,
            StringComparison.Ordinal);
    }

    private static WatcherDbContext CreateContext()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only;Password=model_only")
            .Options;
        return new WatcherDbContext(options);
    }
}
