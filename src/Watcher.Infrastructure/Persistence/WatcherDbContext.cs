using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;

namespace Watcher.Infrastructure.Persistence;

public sealed class WatcherDbContext(DbContextOptions<WatcherDbContext> options) : DbContext(options)
{
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<SoundPreset> SoundPresets => Set<SoundPreset>();
    public DbSet<AlertCommand> AlertCommands => Set<AlertCommand>();
    public DbSet<CommandTarget> CommandTargets => Set<CommandTarget>();
    public DbSet<LocationCommandSequence> LocationCommandSequences => Set<LocationCommandSequence>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<MinuteOfSilenceOccurrence> MinuteOfSilenceOccurrences => Set<MinuteOfSilenceOccurrence>();
    public DbSet<CommandDelivery> CommandDeliveries => Set<CommandDelivery>();
    public DbSet<DeviceCommandReceipt> DeviceCommandReceipts => Set<DeviceCommandReceipt>();
    public DbSet<UkraineAlarmWebhookOperation> UkraineAlarmWebhookOperations => Set<UkraineAlarmWebhookOperation>();
    public DbSet<ExternalIntegrationState> ExternalIntegrationStates => Set<ExternalIntegrationState>();
    public DbSet<AutomaticAlertDispatchState> AutomaticAlertDispatchStates => Set<AutomaticAlertDispatchState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Location>(entity =>
        {
            entity.ToTable("locations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
        });

        modelBuilder.Entity<UserAccount>(entity =>
        {
            entity.ToTable("users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalSubject).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => x.ExternalSubject).IsUnique();
            entity.Property(x => x.Email).HasMaxLength(320).IsRequired();
        });

        modelBuilder.Entity<SoundPreset>(entity =>
        {
            entity.ToTable("sound_presets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ManifestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IsPublished).IsConcurrencyToken();
        });

        modelBuilder.Entity<AlertCommand>(entity =>
        {
            entity.ToTable("alert_commands", table => table.HasCheckConstraint(
                "CK_alert_commands_device_payload",
                "(\"Action\" = 'start' AND \"PresetCode\" IS NOT NULL AND \"TargetCommandId\" IS NULL AND \"MaximumPlaybackDurationSeconds\" BETWEEN 1 AND 900) OR " +
                "(\"Action\" = 'stop' AND \"PresetCode\" IS NULL AND \"TargetCommandId\" IS NOT NULL AND \"MaximumPlaybackDurationSeconds\" = 0)"));
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.Property(x => x.Action).HasMaxLength(20).IsRequired();
            entity.Property(x => x.PresetCode).HasMaxLength(80);
            entity.Property(x => x.MaximumPlaybackDurationSeconds)
                .HasDefaultValue(AlertCommand.DefaultMaximumPlaybackDurationSeconds)
                .HasSentinel(-1)
                .IsRequired();
            entity.HasOne<AlertCommand>().WithMany().HasForeignKey(x => x.TargetCommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.Property(x => x.ExpiresAtUtc).IsRequired();
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasMany(x => x.Targets).WithOne().HasForeignKey(x => x.CommandId);
        });

        modelBuilder.Entity<MinuteOfSilenceOccurrence>(entity =>
        {
            entity.ToTable("minute_of_silence_occurrences");
            entity.HasKey(x => x.RunDate);
            entity.Property(x => x.RunDate).HasColumnName("run_date");
            entity.Property(x => x.Outcome).HasMaxLength(20).IsRequired();
            entity.Property(x => x.SkipReason).HasMaxLength(60);
            entity.HasIndex(x => x.CommandId).IsUnique();
            entity.HasOne<AlertCommand>().WithMany().HasForeignKey(x => x.CommandId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommandTarget>(entity =>
        {
            entity.ToTable("command_targets");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.CommandId, x.LocationId }).IsUnique();
            entity.HasIndex(x => new { x.LocationId, x.Sequence }).IsUnique();
            entity.ToTable(table => table.HasCheckConstraint(
                "CK_command_targets_sequence_positive", "\"Sequence\" > 0"));
            entity.Property(x => x.Sequence).HasDefaultValue(0L);
            entity.Property(x => x.State).HasMaxLength(30).IsRequired();
        });

        modelBuilder.Entity<LocationCommandSequence>(entity =>
        {
            entity.ToTable("location_command_sequences", table => table.HasCheckConstraint(
                "CK_location_command_sequences_next_sequence_positive", "\"NextSequence\" > 0"));
            entity.HasKey(x => x.LocationId);
            entity.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Role>(entity =>
        {
            entity.ToTable("roles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.Property(x => x.Code).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(100).IsRequired();
        });

        modelBuilder.Entity<UserRole>(entity =>
        {
            entity.ToTable("user_roles");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.UserId, x.RoleId, x.LocationId }).IsUnique();
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId);
            entity.HasOne<Role>().WithMany().HasForeignKey(x => x.RoleId);
            entity.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ActorType).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => x.OccurredAtUtc);
        });

        modelBuilder.Entity<Device>(entity =>
        {
            entity.ToTable("devices");
            entity.HasKey(x => x.Id);
            entity.HasOne<Location>().WithMany().HasForeignKey(x => x.LocationId);
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.CertificateThumbprint).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PendingCertificateThumbprint).HasMaxLength(128);
            entity.HasIndex(x => x.CertificateThumbprint).IsUnique();
            entity.HasIndex(x => x.PendingCertificateThumbprint).IsUnique();
        });

        modelBuilder.Entity<DeviceSession>(entity =>
        {
            entity.ToTable("device_sessions");
            entity.HasKey(x => x.Id);
            entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId);
            entity.HasIndex(x => new { x.DeviceId, x.LastSeenAtUtc });
        });

        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            entity.HasOne<AlertCommand>().WithMany().HasForeignKey(x => x.CommandId);
            entity.HasIndex(x => x.CommandId).IsUnique();
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.CreatedAtUtc });
        });

        modelBuilder.Entity<CommandDelivery>(entity =>
        {
            entity.ToTable("command_deliveries", table =>
            {
                table.HasCheckConstraint("CK_command_deliveries_attempt_count", "\"AttemptCount\" >= 0");
                table.HasCheckConstraint("CK_command_deliveries_state",
                    "\"State\" IN ('Pending', 'InFlight', 'Accepted', 'Playing', 'Completed', 'Stopped', 'Rejected', 'Failed', 'Expired')");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.State).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.HasOne<AlertCommand>().WithMany().HasForeignKey(x => x.CommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.CommandId, x.DeviceId }).IsUnique();
            entity.HasIndex(x => new { x.State, x.NextAttemptAtUtc, x.ExpiresAtUtc });
        });

        modelBuilder.Entity<DeviceCommandReceipt>(entity =>
        {
            entity.ToTable("device_command_receipts", table =>
            {
                table.HasCheckConstraint("CK_device_command_receipts_sequence", "\"Sequence\" > 0");
                table.HasCheckConstraint("CK_device_command_receipts_state",
                    "\"State\" IN ('Accepted', 'Playing', 'Completed', 'Stopped', 'Rejected', 'Failed', 'Expired')");
            });
            entity.HasKey(x => new { x.DeviceId, x.EventId });
            entity.Property(x => x.State).HasMaxLength(20).IsRequired();
            entity.Property(x => x.DiagnosticCode).HasMaxLength(64);
            entity.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AlertCommand>().WithMany().HasForeignKey(x => x.CommandId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.CommandId, x.DeviceId, x.ReceivedAtUtc });
        });

        modelBuilder.Entity<UkraineAlarmWebhookOperation>(entity =>
        {
            entity.ToTable("ukrainealarm_webhook_operations", table =>
            {
                table.HasCheckConstraint(
                    "CK_ukrainealarm_webhook_operations_action",
                    "\"Action\" IN ('register', 'update')");
                table.HasCheckConstraint(
                    "CK_ukrainealarm_webhook_operations_state",
                    "\"State\" IN ('Pending', 'Running', 'Succeeded', 'Rejected', 'Unknown', 'Resolved')");
            });
            entity.HasKey(item => item.Id);
            entity.HasIndex(item => item.IdempotencyKey).IsUnique();
            entity.HasIndex(item => new { item.ActorUserId, item.CreatedAtUtc });
            entity.HasIndex(item => item.IntegrationCode)
                .IsUnique()
                .HasFilter("\"State\" IN ('Pending', 'Running', 'Unknown')");
            entity.Property(item => item.IntegrationCode)
                .HasDefaultValue(UkraineAlarmWebhookOperation.DefaultIntegrationCode)
                .HasMaxLength(80)
                .IsRequired();
            entity.Property(item => item.Action).HasMaxLength(20).IsRequired();
            entity.Property(item => item.State).HasMaxLength(20).IsRequired();
            entity.Property(item => item.CallbackUrl).HasMaxLength(2048).IsRequired();
            entity.Property(item => item.OutcomeCode).HasMaxLength(80);
            entity.HasOne<UserAccount>().WithMany().HasForeignKey(item => item.ActorUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ExternalIntegrationState>().WithMany()
                .HasForeignKey(item => item.IntegrationCode)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExternalIntegrationState>(entity =>
        {
            entity.ToTable("external_integration_states", table => table.HasCheckConstraint(
                "CK_external_integration_states_integration_code",
                "\"IntegrationCode\" = 'ukrainealarm_webhook'"));
            entity.HasKey(item => item.IntegrationCode);
            entity.Property(item => item.IntegrationCode).HasMaxLength(80).IsRequired();
        });

        modelBuilder.Entity<AutomaticAlertDispatchState>(entity =>
        {
            entity.ToTable("automatic_alert_dispatch_states", table =>
            {
                table.HasCheckConstraint("CK_automatic_alert_dispatch_states_id",
                    "\"Id\" = 'kremenchuk'");
                table.HasCheckConstraint("CK_automatic_alert_dispatch_states_episode",
                    "(\"HasOpenEpisode\" AND \"Scope\" IS NOT NULL AND \"ScopeId\" IS NOT NULL AND \"StartCommandId\" IS NOT NULL AND \"StartedAtUtc\" IS NOT NULL) OR (NOT \"HasOpenEpisode\" AND \"Scope\" IS NULL AND \"ScopeId\" IS NULL AND \"StartCommandId\" IS NULL AND \"StartedAtUtc\" IS NULL)");
            });
            entity.HasKey(item => item.Id);
            entity.Property(item => item.Id).HasMaxLength(40).IsRequired();
            entity.Property(item => item.Scope).HasMaxLength(16);
            entity.Property(item => item.ScopeId).HasMaxLength(80);
            entity.Property(item => item.LastDecisionCode).HasMaxLength(80).IsRequired();
        });
    }
}
