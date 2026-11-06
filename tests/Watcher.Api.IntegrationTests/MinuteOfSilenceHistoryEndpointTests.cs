using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class MinuteOfSilenceHistoryEndpointTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public MinuteOfSilenceHistoryEndpointTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task History_is_limited_to_authorized_global_roles_and_returns_only_stored_result_fields()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        DateTimeOffset processedAt = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);
        await SeedOccurrencesAsync(
            (new DateOnly(2026, 10, 10), "queued", null, processedAt.AddDays(-2)),
            (new DateOnly(2026, 10, 11), "skipped", "missed_window", processedAt.AddDays(-1)),
            (new DateOnly(2026, 10, 12), "queued", null, processedAt));

        using HttpResponseMessage anonymous = await _fixture.Client.GetAsync(HistoryPath("2026-10-10", "2026-10-12"));
        using HttpResponseMessage operatorResponse = await SendGetAsync(
            seed.OperatorId, "notificator", HistoryPath("2026-10-10", "2026-10-12"));
        using HttpResponseMessage administratorResponse = await SendGetAsync(
            seed.AdministratorId, "admin", HistoryPath("2026-10-10", "2026-10-12"));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, administratorResponse.StatusCode);
        Assert.Equal("no-store", administratorResponse.Headers.CacheControl?.ToString());

        using var body = JsonDocument.Parse(await administratorResponse.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        Assert.Equal(3, root.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, items.Length);
        Assert.Equal("2026-10-12", items[0].GetProperty("runDate").GetString());
        Assert.Equal("queued", items[0].GetProperty("outcome").GetString());
        Assert.Equal(processedAt, items[0].GetProperty("processedAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, items[0].GetProperty("skipReason").ValueKind);
        Assert.Equal("missed_window", items[1].GetProperty("skipReason").GetString());
        Assert.False(items[0].TryGetProperty("commandId", out _));
    }

    [PostgresFact]
    public async Task History_supports_pagination_and_rejects_ranges_over_90_days_or_in_the_future()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        await SeedOccurrencesAsync(
            (new DateOnly(2026, 10, 10), "queued", null, DateTimeOffset.UtcNow.AddDays(-2)),
            (new DateOnly(2026, 10, 11), "skipped", "outside_execution_window", DateTimeOffset.UtcNow.AddDays(-1)));

        using HttpResponseMessage firstPage = await SendGetAsync(seed.AdministratorId, "admin",
            HistoryPath("2026-10-01", "2026-10-31", page: 1, pageSize: 1));
        using var pageBody = JsonDocument.Parse(await firstPage.Content.ReadAsStringAsync());
        using HttpResponseMessage tooLong = await SendGetAsync(seed.AdministratorId, "admin",
            HistoryPath("2026-01-01", "2026-04-01"));
        using HttpResponseMessage future = await SendGetAsync(seed.AdministratorId, "admin",
            HistoryPath("2026-11-01", "2099-01-01"));

        Assert.Equal(HttpStatusCode.OK, firstPage.StatusCode);
        Assert.Equal(2, pageBody.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal("2026-10-11", pageBody.RootElement.GetProperty("items")[0].GetProperty("runDate").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
    }

    [PostgresFact]
    public async Task History_accepts_exactly_90_inclusive_days_and_rejects_invalid_paging()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);

        using HttpResponseMessage ninetyDays = await SendGetAsync(seed.AdministratorId, "admin",
            HistoryPath("2026-01-01", "2026-03-31"));
        using HttpResponseMessage invalidPage = await SendGetAsync(seed.AdministratorId, "admin",
            HistoryPath("2026-03-30", "2026-03-31", page: 0));

        Assert.Equal(HttpStatusCode.OK, ninetyDays.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalidPage.StatusCode);
    }

    [PostgresFact]
    public async Task Global_auditor_can_read_history()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var auditorId = Guid.NewGuid();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.Users.Add(new UserAccount
            {
                Id = auditorId,
                ExternalSubject = "history-auditor",
                Email = "history-auditor@example.test"
            });
            Role auditor = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Аудитор" };
            db.Roles.Add(auditor);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = auditorId,
                RoleId = auditor.Id
            });
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage response = await SendGetAsync(
            auditorId, "auditor", HistoryPath("2026-10-01", "2026-10-10"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task SeedOccurrencesAsync(params (DateOnly RunDate, string Outcome, string? SkipReason,
        DateTimeOffset ProcessedAtUtc)[] occurrences)
    {
        using WatcherDbContext db = _fixture.CreateDbContext();
        db.MinuteOfSilenceOccurrences.AddRange(occurrences.Select(item => new MinuteOfSilenceOccurrence
        {
            RunDate = item.RunDate,
            Outcome = item.Outcome,
            SkipReason = item.SkipReason,
            ProcessedAtUtc = item.ProcessedAtUtc
        }));
        await db.SaveChangesAsync();
    }

    private Task<HttpResponseMessage> SendGetAsync(Guid userId, string role, string path)
    {
        HttpRequestMessage request = new(HttpMethod.Get, path);
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", role);
        return _fixture.Client.SendAsync(request);
    }

    private static string HistoryPath(string from, string to, int page = 1, int pageSize = 25) =>
        $"/api/v1/minute-of-silence/history?from={from}&to={to}&page={page}&pageSize={pageSize}";
}
