using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.AlertSources;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class AlertSourceObservationAuditWriterTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public AlertSourceObservationAuditWriterTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Record_changes_writes_initial_and_changed_states_but_skips_poll_timestamps()
    {
        await _fixture.ResetAndSeedAsync();
        await ClearObservationStatesAsync();
        DateTimeOffset checkedAt = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        AlertSourceObservation initial = CreateObservation(checkedAt);

        await RecordAsync(initial);
        await RecordAsync(initial with { CheckedAtUtc = checkedAt.AddMinutes(1) });
        await RecordAsync(initial with
        {
            AlertLevel = "yellow",
            CheckedAtUtc = checkedAt.AddMinutes(2),
            DataUpdatedAtUtc = checkedAt.AddMinutes(2)
        });
        await RecordAsync(initial with
        {
            AlertLevel = "red",
            CheckedAtUtc = checkedAt.AddMinutes(1),
            DataUpdatedAtUtc = checkedAt.AddMinutes(1)
        });

        await using WatcherDbContext db = _fixture.CreateDbContext();
        AuditEvent[] events = await db.AuditEvents
            .Where(item => item.EventType == "integration.alert_source.state_changed")
            .OrderBy(item => item.OccurredAtUtc)
            .ToArrayAsync();
        Assert.Equal(2, events.Length);
        using var initialPayload = JsonDocument.Parse(events[0].Payload);
        Assert.Equal("initial", initialPayload.RootElement.GetProperty("ChangeType").GetString());
        Assert.Equal(AlertSourceCatalog.UkraineAlarmCode,
            initialPayload.RootElement.GetProperty("Source").GetString());
        Assert.False(events[0].Payload.Contains("token", StringComparison.OrdinalIgnoreCase));

        using var changedPayload = JsonDocument.Parse(events[1].Payload);
        Assert.Equal("changed", changedPayload.RootElement.GetProperty("ChangeType").GetString());
        Assert.Equal("red", changedPayload.RootElement.GetProperty("PreviousAlertLevel").GetString());
        Assert.Equal("yellow", changedPayload.RootElement.GetProperty("AlertLevel").GetString());
        Assert.Equal(checkedAt.AddMinutes(2),
            (await db.AlertSourceObservationStates.SingleAsync()).CheckedAtUtc);
    }

    [PostgresFact]
    public async Task Concurrent_refreshes_write_one_event_for_the_same_state_transition()
    {
        await _fixture.ResetAndSeedAsync();
        await ClearObservationStatesAsync();
        AlertSourceObservation observation = CreateObservation(
            new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

        await Task.WhenAll(RecordAsync(observation), RecordAsync(observation));

        await using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.AuditEvents.CountAsync(
            item => item.EventType == "integration.alert_source.state_changed"));
    }

    private async Task RecordAsync(AlertSourceObservation observation)
    {
        await using WatcherDbContext db = _fixture.CreateDbContext();
        await new AlertSourceObservationAuditWriter(db, TimeProvider.System)
            .RecordChangesAsync([observation], CancellationToken.None);
    }

    private async Task ClearObservationStatesAsync()
    {
        await using WatcherDbContext db = _fixture.CreateDbContext();
        await db.AlertSourceObservationStates.ExecuteDeleteAsync();
    }

    private static AlertSourceObservation CreateObservation(DateTimeOffset checkedAtUtc) => new(
        AlertSourceCatalog.UkraineAlarmCode,
        AlertSourceCatalog.UkraineAlarmName,
        AlertScope.Oblast,
        "UA-53",
        "Полтавська область",
        AlertState.Active,
        "red",
        AlertFreshness.Fresh,
        checkedAtUtc,
        checkedAtUtc.AddMinutes(-1),
        null,
        "https://alerts.example.test/webhook?token=must-not-be-audited");
}
