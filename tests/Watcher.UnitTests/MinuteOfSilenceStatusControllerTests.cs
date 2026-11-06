using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class MinuteOfSilenceStatusControllerTests
{
    [Fact]
    public async Task GetToday_uses_kyiv_date_and_returns_empty_status_when_today_has_no_occurrence()
    {
        DateTimeOffset nowUtc = new(2026, 1, 1, 22, 30, 0, TimeSpan.Zero);
        DateOnly kyivDate = new(2026, 1, 2);
        await using WatcherDbContext db = CreateDbContext();
        Guid userId = await AddGlobalRoleAsync(db, "auditor");
        db.MinuteOfSilenceOccurrences.Add(new MinuteOfSilenceOccurrence
        {
            RunDate = new DateOnly(2026, 1, 1),
            Outcome = "queued",
            CommandId = Guid.NewGuid(),
            ProcessedAtUtc = nowUtc.AddHours(-1)
        });
        await db.SaveChangesAsync();
        MinuteOfSilenceStatusController controller = CreateController(db, userId, nowUtc);

        IActionResult result = await controller.GetToday(CancellationToken.None);

        OkObjectResult response = Assert.IsType<OkObjectResult>(result);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        using JsonDocument body = SerializeResponse(response.Value);
        JsonElement status = body.RootElement;
        Assert.Equal(kyivDate.ToString("yyyy-MM-dd"), status.GetProperty("runDate").GetString());
        Assert.Equal("Europe/Kyiv", status.GetProperty("timeZone").GetString());
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 7, 0, 0, TimeSpan.Zero),
            status.GetProperty("scheduledAtUtc").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("outcome").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("skipReason").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("commandId").ValueKind);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("processedAtUtc").ValueKind);
    }

    [Fact]
    public async Task GetToday_returns_persisted_skip_reason_to_global_auditor()
    {
        var nowUtc = new DateTimeOffset(2026, 11, 5, 8, 30, 0, TimeSpan.Zero);
        DateOnly kyivDate = new(2026, 11, 5);
        var commandId = Guid.NewGuid();
        DateTimeOffset processedAtUtc = nowUtc.AddMinutes(-5);
        await using WatcherDbContext db = CreateDbContext();
        Guid userId = await AddGlobalRoleAsync(db, "auditor");
        db.MinuteOfSilenceOccurrences.Add(new MinuteOfSilenceOccurrence
        {
            RunDate = kyivDate,
            Outcome = "skipped",
            SkipReason = "outside_execution_window",
            ProcessedAtUtc = processedAtUtc
        });
        await db.SaveChangesAsync();
        MinuteOfSilenceStatusController controller = CreateController(db, userId, nowUtc);

        IActionResult result = await controller.GetToday(CancellationToken.None);

        OkObjectResult response = Assert.IsType<OkObjectResult>(result);
        using JsonDocument body = SerializeResponse(response.Value);
        JsonElement status = body.RootElement;
        Assert.Equal("skipped", status.GetProperty("outcome").GetString());
        Assert.Equal("outside_execution_window", status.GetProperty("skipReason").GetString());
        Assert.Equal(JsonValueKind.Null, status.GetProperty("commandId").ValueKind);
        Assert.Equal(processedAtUtc, status.GetProperty("processedAtUtc").GetDateTimeOffset());
    }

    private static async Task<Guid> AddGlobalRoleAsync(WatcherDbContext db, string roleCode)
    {
        var userId = Guid.NewGuid();
        Role role = new() { Id = Guid.NewGuid(), Code = roleCode, Name = roleCode };
        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = $"test-{userId:N}",
            Email = $"{userId:N}@example.test"
        });
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();
        return userId;
    }

    private static WatcherDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static MinuteOfSilenceStatusController CreateController(
        WatcherDbContext db,
        Guid userId,
        DateTimeOffset nowUtc)
    {
        ClaimsIdentity identity = new([new Claim("kgta_user_id", userId.ToString())], "UnitTest");
        return new MinuteOfSilenceStatusController(db, new FixedTimeProvider(nowUtc))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    private static JsonDocument SerializeResponse(object? response) =>
        JsonSerializer.SerializeToDocument(response, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class FixedTimeProvider(DateTimeOffset nowUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => nowUtc;
    }
}
