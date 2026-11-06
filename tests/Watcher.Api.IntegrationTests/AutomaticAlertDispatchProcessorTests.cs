using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.AlertSources;
using Watcher.Api.Commands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Delivery;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class AutomaticAlertDispatchProcessorTests : IClassFixture<PostgresApiFixture>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 8, 0, 0, TimeSpan.Zero);
    private readonly PostgresApiFixture _fixture;

    public AutomaticAlertDispatchProcessorTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Status_is_available_to_global_administrator_but_not_location_operator()
    {
        SeedData seed = await ResetEnvironmentAsync(includeGlobalAdministrator: true);
        using HttpRequestMessage administratorRequest = new(HttpMethod.Get,
            "/api/v1/automatic-alert-dispatch/status");
        administratorRequest.Headers.Add("X-Test-User-Id", seed.AdministratorId.ToString());

        using HttpResponseMessage administratorResponse = await _fixture.Client.SendAsync(administratorRequest);
        Assert.Equal(HttpStatusCode.OK, administratorResponse.StatusCode);
        Assert.Contains("no-store", administratorResponse.Headers.CacheControl?.ToString(),
            StringComparison.OrdinalIgnoreCase);
        using var status = JsonDocument.Parse(await administratorResponse.Content.ReadAsStringAsync());
        Assert.False(status.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal("idle", status.RootElement.GetProperty("state").GetString());

        using HttpRequestMessage operatorRequest = new(HttpMethod.Get,
            "/api/v1/automatic-alert-dispatch/status");
        operatorRequest.Headers.Add("X-Test-User-Id", seed.OperatorId.ToString());
        using HttpResponseMessage operatorResponse = await _fixture.Client.SendAsync(operatorRequest);
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
    }

    [PostgresFact]
    public async Task Active_alert_creates_one_atomic_system_command_and_repeated_observation_is_deduplicated()
    {
        SeedData seed = await ResetEnvironmentAsync();
        await AddAirRaidPresetAsync();

        AutomaticAlertDispatchResult first = await ProcessAsync(ActiveStatus());
        AutomaticAlertDispatchResult repeat = await ProcessAsync(ActiveStatus());

        Assert.Equal(AutomaticAlertDispatchResult.Started, first);
        Assert.Equal(AutomaticAlertDispatchResult.NoChange, repeat);
        using WatcherDbContext db = _fixture.CreateDbContext();
        AutomaticAlertDispatchState state = await db.AutomaticAlertDispatchStates.SingleAsync();
        AlertCommand command = await db.AlertCommands.Include(item => item.Targets).SingleAsync();
        OutboxMessage outbox = await db.OutboxMessages.SingleAsync();
        AuditEvent audit = await db.AuditEvents.SingleAsync();

        Assert.True(state.HasOpenEpisode);
        Assert.Equal(AlertSourceCatalog.KremenchukHromadaId, state.ScopeId);
        Assert.Equal(command.Id, state.StartCommandId);
        Assert.Equal("start", command.Action);
        Assert.Equal("air_raid", command.PresetCode);
        Assert.Equal(900, command.MaximumPlaybackDurationSeconds);
        Assert.Null(command.InitiatedByUserId);
        Assert.Equal(2, command.Targets.Count);
        Assert.Contains(command.Targets, item => item.LocationId == seed.FirstLocationId);
        Assert.Contains(command.Targets, item => item.LocationId == seed.SecondLocationId);
        Assert.Equal(command.Id, outbox.CommandId);
        Assert.Equal("system", audit.ActorType);
        using var payload = JsonDocument.Parse(audit.Payload);
        Assert.Equal(AlertSourceCatalog.UkraineAlarmCode, payload.RootElement.GetProperty("source").GetString());
        Assert.Equal(AlertSourceCatalog.KremenchukHromadaId,
            payload.RootElement.GetProperty("scopeId").GetString());
    }

    [PostgresFact]
    public async Task Confirmed_inactive_status_creates_one_stop_and_closes_the_episode()
    {
        await ResetEnvironmentAsync();
        await AddAirRaidPresetAsync();
        await ProcessAsync(ActiveStatus());
        Guid[] deviceIds = await MaterializeStartDeliveriesAsync();

        AutomaticAlertDispatchResult result = await ProcessAsync(InactiveStatus(), Now.AddSeconds(1));

        Assert.Equal(AutomaticAlertDispatchResult.Stopped, result);
        using WatcherDbContext db = _fixture.CreateDbContext();
        AutomaticAlertDispatchState state = await db.AutomaticAlertDispatchStates.SingleAsync();
        AlertCommand[] commands = await db.AlertCommands
            .Include(item => item.Targets)
            .OrderBy(item => item.CreatedAtUtc)
            .ToArrayAsync();
        Assert.False(state.HasOpenEpisode);
        Assert.Null(state.StartCommandId);
        Assert.Equal(2, commands.Length);
        Assert.Equal("start", commands[0].Action);
        Assert.Equal("stop", commands[1].Action);
        Assert.Equal(commands[0].Id, commands[1].TargetCommandId);
        Assert.All(commands[0].Targets, item => Assert.Equal(1, item.Sequence));
        Assert.All(commands[1].Targets, item => Assert.Equal(2, item.Sequence));
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
        Assert.Equal(4, await db.CommandDeliveries.CountAsync());
        CommandDelivery[] stopDeliveries = await db.CommandDeliveries
            .Where(item => item.CommandId == commands[1].Id)
            .ToArrayAsync();
        Assert.Equal(deviceIds.Order(), stopDeliveries.Select(item => item.DeviceId).Order());
        Assert.All(stopDeliveries, item => Assert.Equal(CommandDeliveryState.Pending, item.State));
        Assert.Equal(3, await db.AuditEvents.CountAsync());
    }

    [PostgresFact]
    public async Task Inactive_status_before_start_materialization_keeps_episode_open_until_stop_can_be_routed()
    {
        await ResetEnvironmentAsync();
        await AddAirRaidPresetAsync();
        await ProcessAsync(ActiveStatus());

        AutomaticAlertDispatchResult earlyStop = await ProcessAsync(InactiveStatus());

        Assert.Equal(AutomaticAlertDispatchResult.Held, earlyStop);
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Assert.True((await db.AutomaticAlertDispatchStates.SingleAsync()).HasOpenEpisode);
            Assert.Single(db.AlertCommands);
        }

        await MaterializeStartDeliveriesAsync();
        AutomaticAlertDispatchResult retriedStop = await ProcessAsync(InactiveStatus(), Now.AddSeconds(1));

        Assert.Equal(AutomaticAlertDispatchResult.Stopped, retriedStop);
        using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        Assert.False((await verifyDb.AutomaticAlertDispatchStates.SingleAsync()).HasOpenEpisode);
        Assert.Equal(2, await verifyDb.AlertCommands.CountAsync());
        Assert.Equal(4, await verifyDb.CommandDeliveries.CountAsync());
    }

    [PostgresFact]
    public async Task Missing_preset_keeps_episode_closed_and_records_hold_reason()
    {
        await ResetEnvironmentAsync();

        AutomaticAlertDispatchResult result = await ProcessAsync(ActiveStatus());

        Assert.Equal(AutomaticAlertDispatchResult.Held, result);
        using WatcherDbContext db = _fixture.CreateDbContext();
        AutomaticAlertDispatchState state = await db.AutomaticAlertDispatchStates.SingleAsync();
        Assert.False(state.HasOpenEpisode);
        Assert.Equal("preset_unavailable", state.LastDecisionCode);
        Assert.Empty(db.AlertCommands);
        Assert.Empty(db.OutboxMessages);
    }

    [PostgresFact]
    public async Task Concurrent_active_observations_create_only_one_command()
    {
        await ResetEnvironmentAsync();
        await AddAirRaidPresetAsync();

        Task<AutomaticAlertDispatchResult> first = ProcessAsync(ActiveStatus());
        Task<AutomaticAlertDispatchResult> second = ProcessAsync(ActiveStatus());
        AutomaticAlertDispatchResult[] results = await Task.WhenAll(first, second);

        Assert.Single(results, item => item == AutomaticAlertDispatchResult.Started);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Single(db.AlertCommands);
        Assert.Single(db.OutboxMessages);
        Assert.Single(db.AuditEvents);
    }

    private async Task<AutomaticAlertDispatchResult> ProcessAsync(
        AlertSourcesStatusResponse status,
        DateTimeOffset? now = null)
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        AutomaticAlertDispatchProcessor processor = new(
            db,
            new AutomaticAlertDispatchPolicy(),
            new AutomaticAlertDispatchOptions { Enabled = true, PresetCode = "air_raid" },
            new FixedTimeProvider(now ?? Now),
            new PostgresLocationCommandSequenceAllocator(db));
        return await processor.ProcessAsync(status, CancellationToken.None);
    }

    private async Task<Guid[]> MaterializeStartDeliveriesAsync()
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        AlertCommand startCommand = await db.AlertCommands.Include(item => item.Targets).SingleAsync();
        Guid[] deviceIds = startCommand.Targets.Select(_ => Guid.NewGuid()).ToArray();
        for (var index = 0; index < startCommand.Targets.Count; index++)
        {
            CommandTarget target = startCommand.Targets[index];
            db.Devices.Add(new Device
            {
                Id = deviceIds[index],
                LocationId = target.LocationId,
                Name = $"Вузол {index + 1}",
                CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            });
        }

        await db.SaveChangesAsync();
        OutboxMessage startOutbox = await db.OutboxMessages.SingleAsync(item => item.CommandId == startCommand.Id);
        CommandDeliveryMaterializer materializer = new(db, new FixedTimeProvider(Now));
        OutboxMaterializationResult result = await materializer.MaterializeAsync(
            startOutbox.Id, CancellationToken.None);
        Assert.Equal(OutboxMaterializationOutcome.Materialized, result.Outcome);
        return deviceIds;
    }

    private async Task<SeedData> ResetEnvironmentAsync(bool includeGlobalAdministrator = false)
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator);
        using WatcherDbContext db = _fixture.CreateDbContext();
        AutomaticAlertDispatchState state = await db.AutomaticAlertDispatchStates.SingleAsync();
        state.HasOpenEpisode = false;
        state.Scope = null;
        state.ScopeId = null;
        state.StartCommandId = null;
        state.StartedAtUtc = null;
        state.LastDecisionCode = "not_evaluated";
        state.UpdatedAtUtc = Now;
        await db.SaveChangesAsync();
        return seed;
    }

    private async Task AddAirRaidPresetAsync()
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        db.SoundPresets.Add(new SoundPreset
        {
            Id = Guid.NewGuid(),
            Code = "air_raid",
            Name = "Повітряна тривога",
            Priority = 1,
            ManifestHash = new string('b', 64),
            IsPublished = true
        });
        await db.SaveChangesAsync();
    }

    private static AlertSourcesStatusResponse ActiveStatus() => Status("active");

    private static AlertSourcesStatusResponse InactiveStatus() => Status("inactive");

    private static AlertSourcesStatusResponse Status(string level)
    {
        AlertSourceObservationResponse observation = new(
            AlertSourceCatalog.UkraineAlarmCode,
            AlertSourceCatalog.UkraineAlarmName,
            "hromada",
            AlertSourceCatalog.KremenchukHromadaId,
            AlertSourceCatalog.KremenchukHromadaName,
            level,
            null,
            "fresh",
            Now,
            Now,
            null,
            AlertSourceCatalog.UkraineAlarmAttributionUrl,
            null,
            null);
        AlertTargetStatus target = new(
            observation.Scope,
            observation.ScopeId,
            observation.ScopeName,
            level,
            null,
            "fresh",
            Now,
            Now,
            observation,
            [observation]);
        return new AlertSourcesStatusResponse(Now, [target]);
    }

    private sealed class FixedTimeProvider(DateTimeOffset instantUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instantUtc;
    }
}
