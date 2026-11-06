using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Commands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.AlertSources;

public enum AutomaticAlertDispatchResult
{
    Disabled,
    Held,
    NoChange,
    Started,
    Stopped
}

public sealed class AutomaticAlertDispatchProcessor(
    WatcherDbContext db,
    AutomaticAlertDispatchPolicy policy,
    AutomaticAlertDispatchOptions options,
    TimeProvider clock,
    ILocationCommandSequenceAllocator sequenceAllocator)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AutomaticAlertDispatchResult> ProcessAsync(
        AlertSourcesStatusResponse status,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!options.Enabled)
        {
            return AutomaticAlertDispatchResult.Disabled;
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        AutomaticAlertDispatchState state = await db.AutomaticAlertDispatchStates
            .FromSqlInterpolated($"SELECT * FROM automatic_alert_dispatch_states WHERE \"Id\" = {AutomaticAlertDispatchState.KremenchukId} FOR UPDATE")
            .SingleAsync(cancellationToken);
        OpenAlertEpisode? openEpisode = CreateOpenEpisode(state);
        AutomaticAlertDispatchDecision decision = policy.Evaluate(status, openEpisode);
        DateTimeOffset now = clock.GetUtcNow();

        if (decision.Action == AutomaticAlertDispatchAction.Start && !state.HasOpenEpisode)
        {
            AutomaticAlertDispatchResult result = await StartEpisodeAsync(state, decision, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        if (decision.Action == AutomaticAlertDispatchAction.Stop && state.HasOpenEpisode)
        {
            AutomaticAlertDispatchResult result = await StopEpisodeAsync(
                state, decision, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        state.LastDecisionCode = decision.ReasonCode;
        state.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision.Action switch
        {
            AutomaticAlertDispatchAction.Hold => AutomaticAlertDispatchResult.Held,
            _ => AutomaticAlertDispatchResult.NoChange
        };
    }

    private async Task<AutomaticAlertDispatchResult> StartEpisodeAsync(
        AutomaticAlertDispatchState state,
        AutomaticAlertDispatchDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AlertSourceObservationResponse? observation = decision.Observation;
        SoundPreset? preset = await db.SoundPresets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == options.PresetCode && item.IsPublished, cancellationToken);
        if (preset is null || !SoundPreset.HasValidManifestHash(preset.ManifestHash))
        {
            state.LastDecisionCode = preset is null ? "preset_unavailable" : "preset_manifest_invalid";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        List<Location> locations = await db.Locations.AsNoTracking()
            .Where(item => item.IsActive)
            .OrderBy(item => item.Id)
            .ToListAsync(cancellationToken);
        if (locations.Count == 0)
        {
            state.LastDecisionCode = "no_active_locations";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        AlertCommand command = new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = preset.Code,
            MaximumPlaybackDurationSeconds = AlertCommand.DefaultMaximumPlaybackDurationSeconds,
            InitiatedByUserId = null,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(AlertCommand.DefaultValidityMinutes)
        };
        foreach (Location location in locations)
        {
            command.Targets.Add(new CommandTarget
            {
                Id = Guid.NewGuid(),
                LocationId = location.Id,
                Sequence = await sequenceAllocator.AllocateAsync(location.Id, cancellationToken),
                State = "Created"
            });
        }

        var payload = new
        {
            command.Id,
            command.Action,
            command.PresetCode,
            command.ExpiresAtUtc,
            LocationIds = command.Targets.Select(target => target.LocationId),
            decision.ReasonCode,
            Source = observation?.Source,
            Scope = observation?.Scope,
            ScopeId = observation?.ScopeId,
            observation?.CheckedAtUtc,
            observation?.DataUpdatedAtUtc
        };
        AddCommandRecords(command, payload, now);
        state.HasOpenEpisode = true;
        state.Scope = observation?.Scope;
        state.ScopeId = observation?.ScopeId;
        state.StartCommandId = command.Id;
        state.StartedAtUtc = now;
        state.LastDecisionCode = decision.ReasonCode;
        state.UpdatedAtUtc = now;
        return AutomaticAlertDispatchResult.Started;
    }

    private async Task<AutomaticAlertDispatchResult> StopEpisodeAsync(
        AutomaticAlertDispatchState state,
        AutomaticAlertDispatchDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AlertCommand startCommand = await db.AlertCommands
            .Include(item => item.Targets)
            .SingleAsync(item => item.Id == state.StartCommandId, cancellationToken);
        var startIsMaterialized = await db.OutboxMessages.AsNoTracking()
            .AnyAsync(item => item.CommandId == startCommand.Id && item.ProcessedAtUtc != null,
                cancellationToken);
        if (!startIsMaterialized)
        {
            state.LastDecisionCode = "awaiting_start_delivery";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        CommandDelivery[] activeStartDeliveries = await db.CommandDeliveries
            .FromSqlInterpolated($"""
                SELECT delivery.*
                FROM command_deliveries AS delivery
                JOIN devices AS device ON device."Id" = delivery."DeviceId"
                JOIN command_targets AS target
                  ON target."CommandId" = delivery."CommandId"
                 AND target."LocationId" = device."LocationId"
                WHERE delivery."CommandId" = {startCommand.Id}
                  AND delivery."State" IN ('Pending', 'InFlight', 'Accepted', 'Playing')
                FOR UPDATE OF delivery
                """)
            .ToArrayAsync(cancellationToken);
        Guid[] activeDeviceIds = activeStartDeliveries.Select(item => item.DeviceId).ToArray();
        Dictionary<Guid, Guid> routableDeviceLocations = await (
                from device in db.Devices.AsNoTracking()
                join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
                where activeDeviceIds.Contains(device.Id) && device.RevokedAtUtc == null && location.IsActive
                select new { device.Id, device.LocationId })
            .ToDictionaryAsync(item => item.Id, item => item.LocationId, cancellationToken);
        CommandDelivery[] routableStartDeliveries = activeStartDeliveries
            .Where(item => routableDeviceLocations.ContainsKey(item.DeviceId))
            .ToArray();
        if (routableStartDeliveries.Length == 0)
        {
            CloseEpisode(state, decision.ReasonCode, now);
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "automatic_alert.stop_not_required",
                ActorType = "system",
                Payload = JsonSerializer.Serialize(new
                {
                    TargetCommandId = startCommand.Id,
                    decision.ReasonCode,
                    Reason = "no_active_routable_deliveries"
                }, JsonOptions),
                OccurredAtUtc = now
            });
            return AutomaticAlertDispatchResult.Stopped;
        }

        Guid[] locationIds = routableStartDeliveries
            .Select(item => routableDeviceLocations[item.DeviceId])
            .Distinct()
            .Order()
            .ToArray();
        AlertCommand stopCommand = new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            Action = "stop",
            TargetCommandId = startCommand.Id,
            MaximumPlaybackDurationSeconds = 0,
            InitiatedByUserId = null,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(AlertCommand.DefaultValidityMinutes)
        };
        foreach (Guid locationId in locationIds)
        {
            stopCommand.Targets.Add(new CommandTarget
            {
                Id = Guid.NewGuid(),
                LocationId = locationId,
                Sequence = await sequenceAllocator.AllocateAsync(locationId, cancellationToken),
                State = "Created"
            });
        }

        db.CommandDeliveries.AddRange(routableStartDeliveries.Select(delivery => new CommandDelivery
        {
            Id = Guid.NewGuid(),
            CommandId = stopCommand.Id,
            DeviceId = delivery.DeviceId,
            State = CommandDeliveryState.Pending,
            CreatedAtUtc = now,
            ExpiresAtUtc = stopCommand.ExpiresAtUtc,
            NextAttemptAtUtc = now
        }));

        AlertSourceObservationResponse? observation = decision.Observation;
        var payload = new
        {
            stopCommand.Id,
            stopCommand.Action,
            stopCommand.TargetCommandId,
            stopCommand.ExpiresAtUtc,
            LocationIds = stopCommand.Targets.Select(target => target.LocationId),
            decision.ReasonCode,
            Source = observation?.Source,
            Scope = observation?.Scope,
            ScopeId = observation?.ScopeId,
            observation?.CheckedAtUtc,
            observation?.DataUpdatedAtUtc
        };
        AddCommandRecords(stopCommand, payload, now);
        CloseEpisode(state, decision.ReasonCode, now);
        return AutomaticAlertDispatchResult.Stopped;
    }

    private void AddCommandRecords(AlertCommand command, object payload, DateTimeOffset now)
    {
        var serializedPayload = JsonSerializer.Serialize(payload, JsonOptions);
        db.AlertCommands.Add(command);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CommandId = command.Id,
            EventType = "alert_command.created",
            Payload = serializedPayload,
            CreatedAtUtc = now
        });
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = command.Action == "stop" ? "alert_command.stop_requested" : "alert_command.created",
            ActorType = "system",
            Payload = serializedPayload,
            OccurredAtUtc = now
        });
    }

    private static void CloseEpisode(
        AutomaticAlertDispatchState state,
        string decisionCode,
        DateTimeOffset now)
    {
        state.HasOpenEpisode = false;
        state.Scope = null;
        state.ScopeId = null;
        state.StartCommandId = null;
        state.StartedAtUtc = null;
        state.LastDecisionCode = decisionCode;
        state.UpdatedAtUtc = now;
    }

    private static OpenAlertEpisode? CreateOpenEpisode(AutomaticAlertDispatchState state)
    {
        if (!state.HasOpenEpisode ||
            !Enum.TryParse(state.Scope, ignoreCase: true, out AlertScope scope) ||
            string.IsNullOrWhiteSpace(state.ScopeId))
        {
            return null;
        }

        return new OpenAlertEpisode(scope, state.ScopeId);
    }
}
