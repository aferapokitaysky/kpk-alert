using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.AlertSources;

public sealed class AlertSourceObservationAuditWriter(WatcherDbContext db, TimeProvider clock)
{
    public async Task RecordChangesAsync(
        IEnumerable<AlertSourceObservation> observations,
        CancellationToken cancellationToken)
    {
        AlertSourceObservation[] snapshot = observations
            .GroupBy(item => new { item.Source, item.Scope, item.ScopeId })
            .Select(group => group.MaxBy(item => item.CheckedAtUtc)!)
            .ToArray();
        if (snapshot.Length == 0)
        {
            return;
        }

        await using IDbContextTransaction transaction = await db.Database
            .BeginTransactionAsync(cancellationToken);
        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_xact_lock(1511506140)", cancellationToken);
        }

        List<AlertSourceObservationState> existingStates = db.Database.IsNpgsql()
            ? await db.AlertSourceObservationStates
                .FromSqlRaw("SELECT * FROM alert_source_observation_states FOR UPDATE")
                .ToListAsync(cancellationToken)
            : await db.AlertSourceObservationStates.ToListAsync(cancellationToken);
        var statesByKey = existingStates.ToDictionary(
            item => (item.Source, item.Scope, item.ScopeId));

        foreach (AlertSourceObservation observation in snapshot)
        {
            (string Source, string Scope, string ScopeId) key =
                (observation.Source, observation.Scope.ToString().ToLowerInvariant(), observation.ScopeId);
            statesByKey.TryGetValue(key, out AlertSourceObservationState? state);
            if (state?.CheckedAtUtc is DateTimeOffset previousCheckedAt &&
                (observation.CheckedAtUtc is not DateTimeOffset currentCheckedAt ||
                 currentCheckedAt < previousCheckedAt))
            {
                continue;
            }

            if (state is null)
            {
                state = new AlertSourceObservationState
                {
                    Source = key.Item1,
                    Scope = key.Item2,
                    ScopeId = key.Item3
                };
                db.AlertSourceObservationStates.Add(state);
                AddAuditEvent(observation, null, "initial");
                statesByKey.Add(key, state);
            }
            else if (HasStateChanged(state, observation))
            {
                AddAuditEvent(observation, state, "changed");
            }

            UpdateState(state, observation);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void AddAuditEvent(
        AlertSourceObservation observation,
        AlertSourceObservationState? previous,
        string changeType)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "integration.alert_source.state_changed",
            ActorType = "integration",
            Payload = JsonSerializer.Serialize(new
            {
                observation.Source,
                Scope = observation.Scope.ToString().ToLowerInvariant(),
                observation.ScopeId,
                Level = observation.State.ToString().ToLowerInvariant(),
                observation.AlertLevel,
                Freshness = observation.Freshness.ToString().ToLowerInvariant(),
                observation.CheckedAtUtc,
                observation.DataUpdatedAtUtc,
                observation.ErrorCode,
                LastKnownLevel = observation.LastKnownState?.ToString().ToLowerInvariant(),
                observation.LastKnownAlertLevel,
                ChangeType = changeType,
                PreviousLevel = previous?.Level.ToLowerInvariant(),
                PreviousAlertLevel = previous?.AlertLevel,
                PreviousFreshness = previous?.Freshness.ToLowerInvariant(),
                PreviousErrorCode = previous?.ErrorCode
            }),
            OccurredAtUtc = observation.CheckedAtUtc ?? clock.GetUtcNow()
        });
    }

    private static bool HasStateChanged(
        AlertSourceObservationState current,
        AlertSourceObservation observation) =>
        current.Level != observation.State.ToString() ||
        current.AlertLevel != observation.AlertLevel ||
        current.Freshness != observation.Freshness.ToString() ||
        current.ErrorCode != observation.ErrorCode ||
        current.LastKnownLevel != observation.LastKnownState?.ToString() ||
        current.LastKnownAlertLevel != observation.LastKnownAlertLevel;

    private static void UpdateState(
        AlertSourceObservationState current,
        AlertSourceObservation observation)
    {
        current.Level = observation.State.ToString();
        current.AlertLevel = observation.AlertLevel;
        current.Freshness = observation.Freshness.ToString();
        current.ErrorCode = observation.ErrorCode;
        current.CheckedAtUtc = observation.CheckedAtUtc;
        current.DataUpdatedAtUtc = observation.DataUpdatedAtUtc;
        current.LastKnownLevel = observation.LastKnownState?.ToString();
        current.LastKnownAlertLevel = observation.LastKnownAlertLevel;
    }
}
