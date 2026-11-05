using System.Data.Common;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Watcher.Api.Commands;
using Watcher.Contracts.DeviceCommands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class CommandReceiptProcessingTests : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private readonly PostgresApiFixture _fixture;

    public CommandReceiptProcessingTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Receipt_transitions_are_recorded_once_with_audit_and_delivery_state()
    {
        ReceiptSeed seed = await CreateReceiptSeedAsync();
        CommandReceipt accepted = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Accepted);
        CommandReceipt playing = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Playing);
        CommandReceipt completed = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Completed);

        Assert.Equal(CommandReceiptOutcome.Recorded, await ProcessAsync(seed.DeviceId, accepted));
        Assert.Equal(CommandReceiptOutcome.Duplicate, await ProcessAsync(seed.DeviceId, accepted));
        Assert.Equal(CommandReceiptOutcome.Conflict, await ProcessAsync(seed.DeviceId, accepted with
        {
            OccurredAtUtc = accepted.OccurredAtUtc.AddSeconds(1)
        }));
        Assert.Equal(CommandReceiptOutcome.Recorded, await ProcessAsync(seed.DeviceId, playing));
        Assert.Equal(CommandReceiptOutcome.Recorded, await ProcessAsync(seed.DeviceId, completed));

        using WatcherDbContext db = _fixture.CreateDbContext();
        CommandDelivery delivery = await db.CommandDeliveries.SingleAsync();
        Assert.Equal(CommandDeliveryState.Completed, delivery.State);
        Assert.Null(delivery.NextAttemptAtUtc);
        Assert.Equal(3, await db.DeviceCommandReceipts.CountAsync());
        Assert.Equal(3, await db.AuditEvents.CountAsync(item => item.EventType == "alert_command.receipt_received"));
    }

    [PostgresFact]
    public async Task Receipt_rejects_wrong_sequence_and_invalid_state_transition()
    {
        ReceiptSeed seed = await CreateReceiptSeedAsync();

        Assert.Equal(CommandReceiptOutcome.Conflict, await ProcessAsync(
            seed.DeviceId, CreateReceipt(seed.CommandId, 2, DeviceCommandState.Accepted)));
        Assert.Equal(CommandReceiptOutcome.Recorded, await ProcessAsync(
            seed.DeviceId, CreateReceipt(seed.CommandId, 1, DeviceCommandState.Accepted)));
        Assert.Equal(CommandReceiptOutcome.Conflict, await ProcessAsync(
            seed.DeviceId, CreateReceipt(seed.CommandId, 1, DeviceCommandState.Completed)));

        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(CommandDeliveryState.Accepted, (await db.CommandDeliveries.SingleAsync()).State);
        Assert.Single(await db.DeviceCommandReceipts.ToArrayAsync());
    }

    [PostgresFact]
    public async Task Concurrent_retries_of_the_same_event_are_deduplicated()
    {
        ReceiptSeed seed = await CreateReceiptSeedAsync();
        CommandReceipt receipt = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Accepted);
        using WatcherDbContext lockContext = _fixture.CreateDbContext();
        var connectionString = lockContext.Database.GetConnectionString()!;
        var applicationName = $"receipt-race-{Guid.NewGuid():N}";

        await using NpgsqlConnection lockConnection = new(connectionString);
        await lockConnection.OpenAsync();
        await using NpgsqlTransaction lockTransaction = await lockConnection.BeginTransactionAsync();
        await using (NpgsqlCommand lockCommand = new(
                         "SELECT \"Id\" FROM devices WHERE \"Id\" = @deviceId FOR UPDATE",
                         lockConnection,
                         lockTransaction))
        {
            lockCommand.Parameters.AddWithValue("deviceId", seed.DeviceId);
            Assert.Equal(seed.DeviceId, await lockCommand.ExecuteScalarAsync());
        }

        var firstInterceptor = new DeviceLockQueryInterceptor();
        var secondInterceptor = new DeviceLockQueryInterceptor();
        Task<CommandReceiptOutcome> first = ProcessWithApplicationNameAsync(
            connectionString, applicationName, firstInterceptor, seed.DeviceId, receipt);
        Task<CommandReceiptOutcome> second = ProcessWithApplicationNameAsync(
            connectionString, applicationName, secondInterceptor, seed.DeviceId, receipt);

        Exception? waitError = null;
        try
        {
            await Task.WhenAll(firstInterceptor.LockQueryStarted, secondInterceptor.LockQueryStarted)
                .WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForLockWaitersAsync(connectionString, applicationName, expectedCount: 2);
        }
        catch (Exception exception)
        {
            waitError = exception;
        }

        await lockTransaction.CommitAsync();
        CommandReceiptOutcome[] outcomes = await Task.WhenAll(first, second);
        if (waitError is not null)
        {
            throw waitError;
        }

        Assert.Single(outcomes, outcome => outcome == CommandReceiptOutcome.Recorded);
        Assert.Single(outcomes, outcome => outcome == CommandReceiptOutcome.Duplicate);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Single(await db.DeviceCommandReceipts.ToArrayAsync());
        Assert.Single(await db.AuditEvents.Where(item => item.EventType == "alert_command.receipt_received").ToArrayAsync());
    }

    [PostgresFact]
    public async Task Downgrade_refuses_to_reduce_a_diagnostic_code_without_losing_data()
    {
        ReceiptSeed seed = await CreateReceiptSeedAsync();
        string diagnosticCode = new('a', 64);
        CommandReceipt receipt = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Failed) with
        {
            DiagnosticCode = diagnosticCode
        };
        Assert.Equal(CommandReceiptOutcome.Recorded, await ProcessAsync(seed.DeviceId, receipt));

        using WatcherDbContext db = _fixture.CreateDbContext();
        IMigrator migrator = db.Database.GetService<IMigrator>();
        PostgresException exception = await Assert.ThrowsAsync<PostgresException>(
            () => migrator.MigrateAsync("20261105105244_AddDeviceCommandReceipts"));

        Assert.Contains("є довші значення", exception.MessageText, StringComparison.Ordinal);
        var preservedCode = await db.DeviceCommandReceipts
            .Where(item => item.EventId == receipt.EventId)
            .Select(item => item.DiagnosticCode)
            .SingleAsync();
        Assert.Equal(diagnosticCode, preservedCode);
    }

    [PostgresFact]
    public async Task Receipt_uses_contract_validation_for_time_and_diagnostic_code()
    {
        ReceiptSeed seed = await CreateReceiptSeedAsync();
        CommandReceipt missingCode = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Failed);
        CommandReceipt invalidCode = missingCode with { DiagnosticCode = "File Missing" };
        CommandReceipt nonUtcTime = CreateReceipt(seed.CommandId, 1, DeviceCommandState.Accepted) with
        {
            OccurredAtUtc = Now.ToOffset(TimeSpan.FromHours(2))
        };
        CommandReceipt maximumLengthCode = missingCode with { DiagnosticCode = new string('a', 64) };

        Assert.Equal(CommandReceiptOutcome.Invalid, await ProcessAsync(seed.DeviceId, missingCode));
        Assert.Equal(CommandReceiptOutcome.Invalid, await ProcessAsync(seed.DeviceId, invalidCode));
        Assert.Equal(CommandReceiptOutcome.Invalid, await ProcessAsync(seed.DeviceId, nonUtcTime));
        Assert.Equal(CommandReceiptOutcome.Recorded, await ProcessAsync(seed.DeviceId, maximumLengthCode));
    }

    private async Task<CommandReceiptOutcome> ProcessAsync(Guid deviceId, CommandReceipt receipt)
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        return await new CommandReceiptProcessor(db, new FixedTimeProvider(Now))
            .ProcessAsync(deviceId, receipt, CancellationToken.None);
    }

    private static async Task<CommandReceiptOutcome> ProcessWithApplicationNameAsync(
        string connectionString,
        string applicationName,
        DeviceLockQueryInterceptor interceptor,
        Guid deviceId,
        CommandReceipt receipt)
    {
        NpgsqlConnectionStringBuilder connection = new(connectionString) { ApplicationName = applicationName };
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql(connection.ConnectionString)
            .AddInterceptors(interceptor)
            .Options;
        using WatcherDbContext db = new(options);
        return await new CommandReceiptProcessor(db, new FixedTimeProvider(Now))
            .ProcessAsync(deviceId, receipt, CancellationToken.None);
    }

    private static async Task WaitForLockWaitersAsync(
        string connectionString,
        string applicationName,
        int expectedCount)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = new("""
            SELECT count(*)
            FROM pg_stat_activity
            WHERE application_name = @applicationName
              AND state = 'active'
              AND wait_event_type = 'Lock'
            """, connection);
        command.Parameters.AddWithValue("applicationName", applicationName);
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            var lockWaiters = (long)(await command.ExecuteScalarAsync())!;
            if (lockWaiters >= expectedCount)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25));
        }

        throw new TimeoutException($"Не вдалося дочекатися {expectedCount} запитів, заблокованих на рядку пристрою.");
    }

    private async Task<ReceiptSeed> CreateReceiptSeedAsync()
    {
        SeedData locations = await _fixture.ResetAndSeedAsync();
        var deviceId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        using WatcherDbContext db = _fixture.CreateDbContext();
        db.Devices.Add(new Device
        {
            Id = deviceId,
            LocationId = locations.FirstLocationId,
            Name = "Вузол головного корпусу",
            CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray())
        });
        db.AlertCommands.Add(new AlertCommand
        {
            Id = commandId,
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            InitiatedByUserId = locations.OperatorId,
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddMinutes(5)
        });
        db.CommandTargets.Add(new CommandTarget
        {
            Id = Guid.NewGuid(),
            CommandId = commandId,
            LocationId = locations.FirstLocationId,
            Sequence = 1,
            State = "Created"
        });
        db.CommandDeliveries.Add(new CommandDelivery
        {
            Id = Guid.NewGuid(),
            CommandId = commandId,
            DeviceId = deviceId,
            State = CommandDeliveryState.Pending,
            CreatedAtUtc = Now,
            ExpiresAtUtc = Now.AddMinutes(5),
            NextAttemptAtUtc = Now
        });
        await db.SaveChangesAsync();
        return new(deviceId, commandId);
    }

    private static CommandReceipt CreateReceipt(Guid commandId, long sequence, DeviceCommandState state) =>
        new(1, Guid.NewGuid(), commandId, sequence, state, Now.AddSeconds(1), null);

    private sealed record ReceiptSeed(Guid DeviceId, Guid CommandId);

    private sealed class DeviceLockQueryInterceptor : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _lockQueryStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task LockQueryStarted => _lockQueryStarted.Task;

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                _lockQueryStarted.TrySetResult();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
