using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Commands;
using Watcher.Contracts.DeviceCommands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.AlertSources;

public enum AutomaticAlertDispatchResult
{
    Disabled,
    Held,
    NoChange,
    Started,
    Replaced,
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
        AlertObservationBaseline? baseline = CreateBaseline(state);
        DateTimeOffset now = clock.GetUtcNow();
        var observationGap = state.HasObservationBaseline &&
            (state.NeedsObservationResync ||
             now - state.UpdatedAtUtc > TimeSpan.FromTicks(options.PollInterval.Ticks * 2));
        AutomaticAlertDispatchDecision evaluatedDecision = policy.Evaluate(status, openEpisode, baseline);
        var previousDecisionCode = state.LastDecisionCode;
        AlertSourceObservationResponse? currentObservation = policy.SelectBaselineObservation(status);
        AutomaticAlertDispatchDecision decision = observationGap
            ? openEpisode is null
                ? policy.Evaluate(status, null, null)
                : new(AutomaticAlertDispatchAction.Hold, "observation_gap_requires_confirmation", null)
            : evaluatedDecision;

        if (observationGap && openEpisode is not null &&
            evaluatedDecision.Action != AutomaticAlertDispatchAction.Hold && currentObservation is not null)
        {
            UpdateBaseline(state, currentObservation);
            state.NeedsObservationResync = false;
        }
        else if (observationGap && openEpisode is not null)
        {
            state.NeedsObservationResync = true;
        }

        if (decision.Action == AutomaticAlertDispatchAction.InitializeBaseline)
        {
            if (decision.Observation is not null)
            {
                UpdateBaseline(state, decision.Observation);
            }

            state.NeedsObservationResync = false;
            state.LastDecisionCode = decision.ReasonCode;
            state.UpdatedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AutomaticAlertDispatchResult.NoChange;
        }

        if (decision.Action is not (AutomaticAlertDispatchAction.Hold or
                AutomaticAlertDispatchAction.Start or AutomaticAlertDispatchAction.Replace) &&
            currentObservation is not null)
        {
            UpdateBaseline(state, currentObservation);
        }

        if (decision.Action == AutomaticAlertDispatchAction.Start && !state.HasOpenEpisode)
        {
            var presetCode = options.GetPresetCode(decision.Observation?.AlertLevel);
            AutomaticAlertDispatchResult result = presetCode is null
                ? HoldForMissingLevelPreset(state, "level_preset_unavailable", decision.Observation, now)
                : await StartEpisodeAsync(state, decision, presetCode, now, cancellationToken);
            if (result == AutomaticAlertDispatchResult.Held)
            {
                AddHoldAudit(state, previousDecisionCode, state.LastDecisionCode, status, now);
            }
            else if (result == AutomaticAlertDispatchResult.Started && decision.Observation is not null)
            {
                UpdateBaseline(state, decision.Observation);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        if (decision.Action == AutomaticAlertDispatchAction.Replace && state.HasOpenEpisode)
        {
            var presetCode = options.GetPresetCode(decision.Observation?.AlertLevel);
            AutomaticAlertDispatchResult result = presetCode is null
                ? HoldForMissingLevelPreset(state, "level_preset_unavailable", decision.Observation, now)
                : await ReplaceEpisodeAsync(state, decision, presetCode, now, cancellationToken);
            if (result == AutomaticAlertDispatchResult.Held)
            {
                AddHoldAudit(state, previousDecisionCode, state.LastDecisionCode, status, now);
            }
            else if (result == AutomaticAlertDispatchResult.Replaced && currentObservation is not null)
            {
                UpdateBaseline(state, currentObservation);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        if (decision.Action == AutomaticAlertDispatchAction.Stop && state.HasOpenEpisode)
        {
            AutomaticAlertDispatchResult result = await StopEpisodeAsync(
                state, decision, now, cancellationToken);
            if (result == AutomaticAlertDispatchResult.Held)
            {
                AddHoldAudit(state, previousDecisionCode, state.LastDecisionCode, status, now);
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        state.LastDecisionCode = decision.ReasonCode;
        state.UpdatedAtUtc = now;
        if (decision.Action == AutomaticAlertDispatchAction.Hold)
        {
            AddHoldAudit(state, previousDecisionCode, decision.ReasonCode, status, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return decision.Action switch
        {
            AutomaticAlertDispatchAction.Hold => AutomaticAlertDispatchResult.Held,
            _ => AutomaticAlertDispatchResult.NoChange
        };
    }

    private void AddHoldAudit(
        AutomaticAlertDispatchState state,
        string? previousDecisionCode,
        string? decisionCode,
        AlertSourcesStatusResponse status,
        DateTimeOffset occurredAtUtc)
    {
        if (string.IsNullOrWhiteSpace(decisionCode))
        {
            return;
        }

        HoldAuditObservation[] observations = status.Targets
            .SelectMany(target => target.Observations)
            .Where(IsAuditableSource)
            .GroupBy(item => new
            {
                item.Source,
                item.Scope,
                item.ScopeId,
                item.Level,
                item.AlertLevel,
                item.Freshness
            })
            .Select(group =>
            {
                AlertSourceObservationResponse latest = group
                    .OrderByDescending(item => item.CheckedAtUtc)
                    .ThenByDescending(item => item.DataUpdatedAtUtc)
                    .First();
                return new HoldAuditObservation(
                    group.Key.Source,
                    group.Key.Scope,
                    group.Key.ScopeId,
                    group.Key.Level,
                    group.Key.AlertLevel,
                    group.Key.Freshness,
                    latest.CheckedAtUtc,
                    latest.DataUpdatedAtUtc);
            })
            .OrderBy(item => item.Source, StringComparer.Ordinal)
            .ThenBy(item => item.Scope, StringComparer.Ordinal)
            .ThenBy(item => item.ScopeId, StringComparer.Ordinal)
            .ThenBy(item => item.Level, StringComparer.Ordinal)
            .ThenBy(item => item.AlertLevel, StringComparer.Ordinal)
            .ThenBy(item => item.Freshness, StringComparer.Ordinal)
            .Take(25)
            .ToArray();
        HoldAuditObservation[] includedObservations = observations.Take(24).ToArray();
        var observationsTruncated = observations.Length > includedObservations.Length;
        var fingerprint = CreateHoldAuditFingerprint(decisionCode, includedObservations, observationsTruncated);
        if (string.Equals(previousDecisionCode, decisionCode, StringComparison.Ordinal) &&
            string.Equals(state.LastHoldAuditFingerprint, fingerprint, StringComparison.Ordinal))
        {
            return;
        }

        state.LastHoldAuditFingerprint = fingerprint;

        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "automatic_alert.held",
            ActorType = "system",
            Payload = JsonSerializer.Serialize(new
            {
                DecisionCode = decisionCode,
                Observations = includedObservations,
                ObservationsTruncated = observationsTruncated
            }, JsonOptions),
            OccurredAtUtc = occurredAtUtc
        });
    }

    private static string CreateHoldAuditFingerprint(
        string decisionCode,
        IReadOnlyList<HoldAuditObservation> observations,
        bool observationsTruncated)
    {
        var snapshot = new
        {
            DecisionCode = decisionCode,
            Observations = observations.Select(item => new
            {
                item.Source,
                item.Scope,
                item.ScopeId,
                item.Level,
                item.AlertLevel,
                item.Freshness
            }),
            ObservationsTruncated = observationsTruncated
        };
        var serialized = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        return Convert.ToHexString(SHA256.HashData(serialized));
    }

    private static bool IsAuditableSource(AlertSourceObservationResponse observation) =>
        observation.Source is AlertSourceCatalog.UkraineAlarmCode or
            AlertSourceCatalog.NeptunCode or
            AlertSourceCatalog.AlertsInUaCode;

    private sealed record HoldAuditObservation(
        string Source,
        string Scope,
        string ScopeId,
        string Level,
        string? AlertLevel,
        string Freshness,
        DateTimeOffset? CheckedAtUtc,
        DateTimeOffset? DataUpdatedAtUtc);

    private async Task<AutomaticAlertDispatchResult> StartEpisodeAsync(
        AutomaticAlertDispatchState state,
        AutomaticAlertDispatchDecision decision,
        string presetCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        AlertSourceObservationResponse? observation = decision.Observation;
        SoundPreset? preset = await db.SoundPresets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == presetCode && item.IsPublished, cancellationToken);
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
            ExpiresAtUtc = now.AddSeconds(options.StartCommandValiditySeconds)
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
            AlertLevel = observation?.AlertLevel,
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
        UpdateAttemptedAlertLevel(state, observation);
        state.LastDecisionCode = decision.ReasonCode;
        state.UpdatedAtUtc = now;
        return AutomaticAlertDispatchResult.Started;
    }

    private async Task<AutomaticAlertDispatchResult> ReplaceEpisodeAsync(
        AutomaticAlertDispatchState state,
        AutomaticAlertDispatchDecision decision,
        string presetCode,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (state.StartCommandId is not { } episodeAnchorId)
        {
            state.LastDecisionCode = "open_episode_anchor_missing";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        var anchorIsMaterialized = await db.OutboxMessages.AsNoTracking()
            .AnyAsync(item => item.CommandId == episodeAnchorId && item.ProcessedAtUtc != null,
                cancellationToken);
        if (!anchorIsMaterialized)
        {
            state.LastDecisionCode = "awaiting_start_delivery";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        CommandDelivery[] activeEpisodeDeliveries = await LoadActiveEpisodeDeliveriesAsync(
            episodeAnchorId, cancellationToken);
        if (activeEpisodeDeliveries.Length == 0)
        {
            state.LastDecisionCode = "no_active_episode_deliveries";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        Guid[] activeEpisodeDeviceIds = activeEpisodeDeliveries
            .Select(item => item.DeviceId)
            .Distinct()
            .ToArray();
        var protocolUpgradeRequired = false;
        foreach (Guid deviceId in activeEpisodeDeviceIds)
        {
            if (await GetLatestCommandSchemaVersionAsync(deviceId, cancellationToken) <
                DeviceCommandValidator.ReplaceSchemaVersion)
            {
                protocolUpgradeRequired = true;
                break;
            }
        }
        if (protocolUpgradeRequired)
        {
            state.LastDecisionCode = "command_protocol_upgrade_required";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        SoundPreset? preset = await db.SoundPresets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == presetCode && item.IsPublished, cancellationToken);
        if (preset is null || !SoundPreset.HasValidManifestHash(preset.ManifestHash))
        {
            state.LastDecisionCode = preset is null ? "preset_unavailable" : "preset_manifest_invalid";
            state.UpdatedAtUtc = now;
            return AutomaticAlertDispatchResult.Held;
        }

        AlertCommand episodeAnchor = await db.AlertCommands
            .Include(item => item.Targets)
            .SingleAsync(item => item.Id == episodeAnchorId && item.Action == "start", cancellationToken);
        var command = new AlertCommand
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = Guid.NewGuid(),
            Action = "replace",
            PresetCode = preset.Code,
            TargetCommandId = episodeAnchorId,
            MaximumPlaybackDurationSeconds = AlertCommand.DefaultMaximumPlaybackDurationSeconds,
            InitiatedByUserId = null,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddSeconds(options.StartCommandValiditySeconds)
        };
        foreach (CommandTarget anchorTarget in episodeAnchor.Targets.OrderBy(item => item.LocationId))
        {
            command.Targets.Add(new CommandTarget
            {
                Id = Guid.NewGuid(),
                LocationId = anchorTarget.LocationId,
                Sequence = await sequenceAllocator.AllocateAsync(anchorTarget.LocationId, cancellationToken),
                State = "Created"
            });
        }

        AlertSourceObservationResponse? observation = decision.Observation;
        var payload = new
        {
            command.Id,
            command.Action,
            command.TargetCommandId,
            command.PresetCode,
            command.ExpiresAtUtc,
            LocationIds = command.Targets.Select(target => target.LocationId),
            decision.ReasonCode,
            Source = observation?.Source,
            AlertLevel = observation?.AlertLevel,
            Scope = observation?.Scope,
            ScopeId = observation?.ScopeId,
            observation?.CheckedAtUtc,
            observation?.DataUpdatedAtUtc
        };
        AddCommandRecords(command, payload, now);
        UpdateAttemptedAlertLevel(state, observation);
        state.LastDecisionCode = decision.ReasonCode;
        state.UpdatedAtUtc = now;
        return AutomaticAlertDispatchResult.Replaced;
    }

    private static AutomaticAlertDispatchResult HoldForMissingLevelPreset(
        AutomaticAlertDispatchState state,
        string reasonCode,
        AlertSourceObservationResponse? observation,
        DateTimeOffset now)
    {
        state.LastDecisionCode = reasonCode;
        state.UpdatedAtUtc = now;
        return AutomaticAlertDispatchResult.Held;
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

        CommandDelivery[] activeEpisodeDeliveries = await LoadActiveEpisodeDeliveriesAsync(
            startCommand.Id, cancellationToken);
        Guid[] activeDeviceIds = activeEpisodeDeliveries.Select(item => item.DeviceId).Distinct().ToArray();
        Dictionary<Guid, Guid> routableDeviceLocations = await (
                from device in db.Devices.AsNoTracking()
                join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
                where activeDeviceIds.Contains(device.Id) && device.RevokedAtUtc == null && location.IsActive
                select new { device.Id, device.LocationId })
            .ToDictionaryAsync(item => item.Id, item => item.LocationId, cancellationToken);
        CommandDelivery[] routableEpisodeDeliveries = activeEpisodeDeliveries
            .Where(item => routableDeviceLocations.ContainsKey(item.DeviceId))
            .ToArray();
        if (routableEpisodeDeliveries.Length == 0)
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

        Guid[] locationIds = routableEpisodeDeliveries
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

        db.CommandDeliveries.AddRange(routableEpisodeDeliveries
            .Select(item => item.DeviceId)
            .Distinct()
            .Select(deviceId => new CommandDelivery
            {
                Id = Guid.NewGuid(),
                CommandId = stopCommand.Id,
                DeviceId = deviceId,
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
            AlertLevel = observation?.AlertLevel,
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
            EventType = command.Action switch
            {
                "stop" => "alert_command.stop_requested",
                "replace" => "alert_command.replace_requested",
                _ => "alert_command.created"
            },
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
        state.LastAttemptedAlertLevel = null;
        state.LastAttemptedAlertLevelScope = null;
        state.LastAttemptedAlertLevelScopeId = null;
        state.LastAttemptedAlertLevelDataUpdatedAtUtc = null;
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

    private static AlertObservationBaseline? CreateBaseline(AutomaticAlertDispatchState state)
    {
        if (!state.HasObservationBaseline || state.LastObservedSource is null ||
            state.LastObservedScope is null || state.LastObservedScopeId is null ||
            state.LastObservedLevel is null)
        {
            return null;
        }

        return new AlertObservationBaseline(
            state.LastObservedSource,
            state.LastObservedScope,
            state.LastObservedScopeId,
            state.LastObservedLevel,
            state.LastObservedAlertLevel,
            state.LastAttemptedAlertLevel,
            state.LastAttemptedAlertLevelScope,
            state.LastAttemptedAlertLevelScopeId,
            state.LastAttemptedAlertLevelDataUpdatedAtUtc);
    }

    private static void UpdateBaseline(
        AutomaticAlertDispatchState state,
        AlertSourceObservationResponse observation)
    {
        state.HasObservationBaseline = true;
        state.LastObservedSource = observation.Source;
        state.LastObservedScope = observation.Scope;
        state.LastObservedScopeId = observation.ScopeId;
        state.LastObservedLevel = observation.Level;
        if (!string.Equals(observation.Level, "active", StringComparison.Ordinal) ||
            observation.AlertLevel is not null)
        {
            state.LastObservedAlertLevel = observation.AlertLevel;
        }
    }

    private static void UpdateAttemptedAlertLevel(
        AutomaticAlertDispatchState state,
        AlertSourceObservationResponse? observation)
    {
        if (observation is null)
        {
            state.LastAttemptedAlertLevel = null;
            state.LastAttemptedAlertLevelScope = null;
            state.LastAttemptedAlertLevelScopeId = null;
            state.LastAttemptedAlertLevelDataUpdatedAtUtc = null;
            return;
        }

        state.LastAttemptedAlertLevel = observation.AlertLevel is "red" or "yellow"
            ? observation.AlertLevel
            : null;
        state.LastAttemptedAlertLevelScope = observation.Scope;
        state.LastAttemptedAlertLevelScopeId = observation.ScopeId;
        state.LastAttemptedAlertLevelDataUpdatedAtUtc = state.LastAttemptedAlertLevel is null
            ? null
            : observation.DataUpdatedAtUtc;
    }

    private async Task<CommandDelivery[]> LoadActiveEpisodeDeliveriesAsync(
        Guid episodeAnchorId,
        CancellationToken cancellationToken) => await db.CommandDeliveries
        .FromSqlInterpolated($"""
            SELECT delivery.*
            FROM command_deliveries AS delivery
            JOIN alert_commands AS command ON command."Id" = delivery."CommandId"
            JOIN devices AS device ON device."Id" = delivery."DeviceId"
            JOIN locations AS location ON location."Id" = device."LocationId"
            JOIN command_targets AS target
              ON target."CommandId" = command."Id"
             AND target."LocationId" = device."LocationId"
            WHERE (command."Id" = {episodeAnchorId} OR
                   (command."Action" = 'replace' AND command."TargetCommandId" = {episodeAnchorId}))
              AND delivery."State" IN ('Pending', 'InFlight', 'Accepted', 'Playing')
              AND device."RevokedAtUtc" IS NULL
              AND location."IsActive" = true
            FOR UPDATE OF delivery
            """)
        .ToArrayAsync(cancellationToken);

    private async Task<int> GetLatestCommandSchemaVersionAsync(
        Guid deviceId,
        CancellationToken cancellationToken) => await db.DeviceSessions.AsNoTracking()
        .Where(item => item.DeviceId == deviceId)
        .OrderByDescending(item => item.ConnectedAtUtc)
        .Select(item => item.CommandSchemaVersion)
        .FirstOrDefaultAsync(cancellationToken);
}
