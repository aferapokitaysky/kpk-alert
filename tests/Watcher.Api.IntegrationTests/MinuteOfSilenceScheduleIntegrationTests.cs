using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Scheduling;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class MinuteOfSilenceScheduleIntegrationTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public MinuteOfSilenceScheduleIntegrationTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Published_preset_creates_one_system_command_with_targets_audit_and_outbox()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        await AddMinuteOfSilencePresetAsync();
        DateOnly runDate = new(2026, 11, 5);

        MinuteOfSilenceProcessingResult result = await ProcessAsync(runDate);

        Assert.Equal(MinuteOfSilenceProcessingResult.Queued, result);
        using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await db.MinuteOfSilenceOccurrences.SingleAsync();
        AlertCommand command = await db.AlertCommands.Include(item => item.Targets).SingleAsync();
        OutboxMessage outbox = await db.OutboxMessages.SingleAsync();
        AuditEvent audit = await db.AuditEvents.SingleAsync();

        Assert.Equal(runDate, occurrence.RunDate);
        Assert.Equal("queued", occurrence.Outcome);
        Assert.Null(occurrence.SkipReason);
        Assert.Equal(command.Id, occurrence.CommandId);
        Assert.Null(command.InitiatedByUserId);
        Assert.Equal("start", command.Action);
        Assert.Equal("minute_of_silence", command.PresetCode);
        DateTimeOffset expectedExpiryUtc = ClockAt(runDate).GetUtcNow() + MinuteOfSilenceSchedule.ExecutionWindow;
        Assert.Equal(expectedExpiryUtc, command.ExpiresAtUtc);
        Assert.Equal(new TimeSpan(9, 1, 0),
            TimeZoneInfo.ConvertTime(command.ExpiresAtUtc,
                TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId)).TimeOfDay);
        Assert.Equal(2, command.Targets.Count);
        Assert.Contains(command.Targets, target => target.LocationId == seed.FirstLocationId);
        Assert.Contains(command.Targets, target => target.LocationId == seed.SecondLocationId);
        Assert.Equal(command.Id, outbox.CommandId);
        Assert.Equal("alert_command.created", outbox.EventType);
        Assert.Null(outbox.ProcessedAtUtc);
        using var outboxPayload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(expectedExpiryUtc,
            outboxPayload.RootElement.GetProperty("ExpiresAtUtc").GetDateTimeOffset());
        Assert.Equal("system", audit.ActorType);
        Assert.Null(audit.ActorId);
        Assert.Equal("alert_command.created", audit.EventType);
        using var payload = JsonDocument.Parse(audit.Payload);
        Assert.Equal(runDate.ToString("yyyy-MM-dd"), payload.RootElement.GetProperty("ScheduledDate").GetString());
    }

    [PostgresFact]
    public async Task Missing_or_unpublished_preset_is_recorded_as_skipped_without_a_command()
    {
        await _fixture.ResetAndSeedAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.SoundPresets.Add(CreateMinuteOfSilencePreset(isPublished: false));
            await db.SaveChangesAsync();
        }

        DateOnly runDate = new(2026, 11, 5);
        MinuteOfSilenceProcessingResult result = await ProcessAsync(runDate, ClockAt(runDate));

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, result);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await readDb.MinuteOfSilenceOccurrences.SingleAsync();
        AuditEvent audit = await readDb.AuditEvents.SingleAsync();
        Assert.Equal("skipped", occurrence.Outcome);
        Assert.Equal("preset_unavailable", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Empty(readDb.AlertCommands);
        Assert.Empty(readDb.OutboxMessages);
        Assert.Equal("minute_of_silence.skipped", audit.EventType);
        Assert.Equal("system", audit.ActorType);
        Assert.Null(audit.ActorId);
    }

    [PostgresFact]
    public async Task No_active_locations_is_recorded_as_skipped_without_a_command()
    {
        await _fixture.ResetAndSeedAsync();
        await AddMinuteOfSilencePresetAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            foreach (Location location in await db.Locations.ToListAsync())
            {
                location.IsActive = false;
            }
            await db.SaveChangesAsync();
        }

        DateOnly runDate = new(2026, 11, 5);
        MinuteOfSilenceProcessingResult result = await ProcessAsync(runDate, ClockAt(runDate));

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, result);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await readDb.MinuteOfSilenceOccurrences.SingleAsync();
        AuditEvent audit = await readDb.AuditEvents.SingleAsync();
        Assert.Equal("no_active_locations", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Empty(readDb.AlertCommands);
        Assert.Empty(readDb.OutboxMessages);
        Assert.Equal("minute_of_silence.skipped", audit.EventType);
    }

    [PostgresFact]
    public async Task Invalid_preset_manifest_is_recorded_as_skipped_without_a_command()
    {
        await _fixture.ResetAndSeedAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.SoundPresets.Add(new SoundPreset
            {
                Id = Guid.NewGuid(),
                Code = "minute_of_silence",
                Name = "Хвилина мовчання",
                Priority = 1,
                ManifestHash = new string('g', 64),
                IsPublished = true
            });
            await db.SaveChangesAsync();
        }

        DateOnly runDate = new(2026, 11, 5);
        MinuteOfSilenceProcessingResult result = await ProcessAsync(runDate);

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, result);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await readDb.MinuteOfSilenceOccurrences.SingleAsync();
        Assert.Equal("preset_manifest_invalid", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Empty(readDb.AlertCommands);
        Assert.Empty(readDb.OutboxMessages);
    }

    [PostgresFact]
    public async Task Startup_after_the_daily_window_persists_an_auditable_missed_outcome()
    {
        await _fixture.ResetAndSeedAsync();
        DateOnly runDate = new(2026, 11, 5);
        using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrenceProcessor processor = new(db, ClockAt(runDate, MinuteOfSilenceSchedule.ExecutionWindow));

        MinuteOfSilenceProcessingResult result = await processor.RecordMissedAsync(runDate, CancellationToken.None);

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, result);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await readDb.MinuteOfSilenceOccurrences.SingleAsync();
        AuditEvent audit = await readDb.AuditEvents.SingleAsync();
        Assert.Equal("skipped", occurrence.Outcome);
        Assert.Equal("missed_window", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Empty(readDb.AlertCommands);
        Assert.Empty(readDb.OutboxMessages);
        Assert.Equal("minute_of_silence.skipped", audit.EventType);
    }

    [PostgresFact]
    public async Task Expiry_migration_backfills_existing_commands_from_creation_time()
    {
        DateTimeOffset createdAtUtc = new(2020, 1, 2, 3, 4, 5, TimeSpan.Zero);

        DateTimeOffset expiresAtUtc = await _fixture.MigrateLegacyCommandExpiryAsync(createdAtUtc);

        Assert.Equal(createdAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes), expiresAtUtc);
        Assert.True(expiresAtUtc < DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task Concurrent_processors_claim_a_date_once_and_commit_related_rows_atomically()
    {
        await _fixture.ResetAndSeedAsync();
        await AddMinuteOfSilencePresetAsync();
        DateOnly runDate = new(2026, 11, 5);
        using WatcherDbContext firstDb = _fixture.CreateDbContext();
        using WatcherDbContext secondDb = _fixture.CreateDbContext();
        TimeProvider clock = ClockAt(runDate);
        MinuteOfSilenceOccurrenceProcessor first = new(firstDb, clock);
        MinuteOfSilenceOccurrenceProcessor second = new(secondDb, clock);
        MinuteOfSilenceProcessingResult[] results = await Task.WhenAll(
            first.ProcessAsync(runDate, CancellationToken.None),
            second.ProcessAsync(runDate, CancellationToken.None));

        Assert.Single(results, result => result == MinuteOfSilenceProcessingResult.Queued);
        Assert.Single(results, result => result == MinuteOfSilenceProcessingResult.AlreadyProcessed);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.MinuteOfSilenceOccurrences.CountAsync());
        Assert.Equal(1, await db.AlertCommands.CountAsync());
        Assert.Equal(2, await db.CommandTargets.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
    }

    [PostgresFact]
    public async Task System_command_is_hidden_from_regular_users_but_available_to_global_admins()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var commandId = Guid.NewGuid();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.AlertCommands.Add(new AlertCommand
            {
                Id = commandId,
                IdempotencyKey = Guid.NewGuid(),
                Action = "start",
                PresetCode = "minute_of_silence",
                InitiatedByUserId = null,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(AlertCommand.DefaultValidityMinutes),
                Targets = [new CommandTarget
                {
                    Id = Guid.NewGuid(),
                    LocationId = seed.FirstLocationId,
                    State = "Created"
                }]
            });
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage regularUser = await GetCommandAsync(seed.OperatorId, commandId);
        using HttpResponseMessage administrator = await GetCommandAsync(seed.AdministratorId, commandId, "admin");

        Assert.Equal(HttpStatusCode.Forbidden, regularUser.StatusCode);
        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
    }

    [PostgresFact]
    public async Task Invocation_after_the_daily_window_is_audited_as_skipped_without_a_command()
    {
        await _fixture.ResetAndSeedAsync();
        await AddMinuteOfSilencePresetAsync();
        DateOnly runDate = new(2026, 11, 5);
        TimeProvider clock = ClockAt(runDate, MinuteOfSilenceSchedule.ExecutionWindow);

        MinuteOfSilenceProcessingResult result = await ProcessAsync(runDate, clock);
        DateOnly futureDate = runDate.AddDays(1);
        MinuteOfSilenceProcessingResult futureResult = await ProcessAsync(futureDate, clock);

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, result);
        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, futureResult);
        using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence[] occurrences = await db.MinuteOfSilenceOccurrences
            .OrderBy(item => item.RunDate)
            .ToArrayAsync();
        AuditEvent[] audits = await db.AuditEvents.OrderBy(item => item.OccurredAtUtc).ToArrayAsync();
        Assert.Equal(new[] { runDate, futureDate }, occurrences.Select(item => item.RunDate));
        Assert.All(occurrences, occurrence =>
        {
            Assert.Equal("skipped", occurrence.Outcome);
            Assert.Equal("outside_execution_window", occurrence.SkipReason);
            Assert.Null(occurrence.CommandId);
        });
        Assert.Empty(db.AlertCommands);
        Assert.Empty(db.OutboxMessages);
        Assert.Equal(2, audits.Length);
        Assert.All(audits, audit => Assert.Equal("minute_of_silence.skipped", audit.EventType));
    }

    [PostgresFact]
    public async Task Processing_that_crosses_the_window_boundary_rolls_back_the_command()
    {
        await _fixture.ResetAndSeedAsync();
        await AddMinuteOfSilencePresetAsync();
        DateOnly runDate = new(2026, 11, 5);
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateTimeOffset occurrenceUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, timeZone);
        TimeProvider clock = new SteppingTimeProvider(
            occurrenceUtc.AddSeconds(59),
            occurrenceUtc.AddSeconds(59),
            occurrenceUtc.AddMinutes(1));

        MinuteOfSilenceProcessingResult result = await ProcessAsync(runDate, clock);

        Assert.Equal(MinuteOfSilenceProcessingResult.Skipped, result);
        using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrence occurrence = await db.MinuteOfSilenceOccurrences.SingleAsync();
        Assert.Equal("skipped", occurrence.Outcome);
        Assert.Equal("outside_execution_window", occurrence.SkipReason);
        Assert.Null(occurrence.CommandId);
        Assert.Empty(db.AlertCommands);
        Assert.Empty(db.OutboxMessages);
        AuditEvent audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("minute_of_silence.skipped", audit.EventType);
    }

    private async Task<MinuteOfSilenceProcessingResult> ProcessAsync(
        DateOnly runDate,
        TimeProvider? clock = null)
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        MinuteOfSilenceOccurrenceProcessor processor = new(db, clock ?? ClockAt(runDate));
        return await processor.ProcessAsync(runDate, CancellationToken.None);
    }

    private static TimeProvider ClockAt(DateOnly runDate, TimeSpan? offset = null)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        return new FixedTimeProvider(
            MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, timeZone) + (offset ?? TimeSpan.Zero));
    }

    private async Task AddMinuteOfSilencePresetAsync()
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        db.SoundPresets.Add(CreateMinuteOfSilencePreset(isPublished: true));
        await db.SaveChangesAsync();
    }

    private static SoundPreset CreateMinuteOfSilencePreset(bool isPublished) => new()
    {
        Id = Guid.NewGuid(),
        Code = "minute_of_silence",
        Name = "Хвилина мовчання",
        Priority = 1,
        ManifestHash = new string('c', 64),
        IsPublished = isPublished
    };

    private async Task<HttpResponseMessage> GetCommandAsync(Guid userId, Guid commandId, string? role = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/alert-commands/{commandId}");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }
        return await _fixture.Client.SendAsync(request);
    }

    private sealed class FixedTimeProvider(DateTimeOffset instantUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instantUtc;
    }

    private sealed class SteppingTimeProvider(params DateTimeOffset[] instantsUtc) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Interlocked.Increment(ref _index) - 1;
            return instantsUtc[Math.Min(index, instantsUtc.Length - 1)];
        }
    }
}
