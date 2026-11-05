using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class MinuteOfSilenceStatusEndpointTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public MinuteOfSilenceStatusEndpointTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Today_status_requires_global_audit_role_and_returns_only_the_current_kyiv_occurrence()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var kyivTimeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        DateOnly runDate = MinuteOfSilenceSchedule.GetLocalDate(nowUtc, kyivTimeZone);
        DateTimeOffset scheduledAtUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, kyivTimeZone);
        var processedAtUtc = DateTimeOffset.FromUnixTimeMilliseconds(
            nowUtc.AddMinutes(-1).ToUnixTimeMilliseconds());
        var commandId = Guid.NewGuid();

        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.AlertCommands.Add(new AlertCommand
            {
                Id = commandId,
                IdempotencyKey = Guid.NewGuid(),
                Action = "start",
                PresetCode = "minute_of_silence",
                CreatedAtUtc = processedAtUtc,
                ExpiresAtUtc = processedAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes)
            });
            db.MinuteOfSilenceOccurrences.Add(new MinuteOfSilenceOccurrence
            {
                RunDate = runDate,
                Outcome = "queued",
                CommandId = commandId,
                ProcessedAtUtc = processedAtUtc
            });
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage anonymous = await _fixture.Client.GetAsync("/api/v1/minute-of-silence/status/today");
        using HttpResponseMessage operatorResponse = await SendGetAsync(seed.OperatorId, "notificator");
        using HttpResponseMessage administratorResponse = await SendGetAsync(seed.AdministratorId, "admin");
        using var body = JsonDocument.Parse(await administratorResponse.Content.ReadAsStringAsync());
        JsonElement status = body.RootElement;

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, administratorResponse.StatusCode);
        Assert.Equal("no-store", administratorResponse.Headers.CacheControl?.ToString());
        Assert.Equal(runDate, DateOnly.Parse(status.GetProperty("runDate").GetString()!));
        Assert.Equal("Europe/Kyiv", status.GetProperty("timeZone").GetString());
        Assert.Equal(scheduledAtUtc, status.GetProperty("scheduledAtUtc").GetDateTimeOffset());
        Assert.Equal("queued", status.GetProperty("outcome").GetString());
        Assert.Equal(commandId, status.GetProperty("commandId").GetGuid());
        Assert.Equal(processedAtUtc, status.GetProperty("processedAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("skipReason").ValueKind);
    }

    private Task<HttpResponseMessage> SendGetAsync(Guid userId, string role)
    {
        HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/minute-of-silence/status/today");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", role);
        return _fixture.Client.SendAsync(request);
    }
}
