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
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<DeviceSession> DeviceSessions => Set<DeviceSession>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<MinuteOfSilenceOccurrence> MinuteOfSilenceOccurrences => Set<MinuteOfSilenceOccurrence>();
    public DbSet<CommandDelivery> CommandDeliveries => Set<CommandDelivery>();

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
        });

        modelBuilder.Entity<AlertCommand>(entity =>
        {
            entity.ToTable("alert_commands");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.IdempotencyKey).IsUnique();
            entity.Property(x => x.Action).HasMaxLength(20).IsRequired();
            entity.Property(x => x.PresetCode).HasMaxLength(80).IsRequired();
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
            entity.Property(x => x.State).HasMaxLength(30).IsRequired();
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
            entity.HasIndex(x => x.CertificateThumbprint).IsUnique();
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
    }
}
