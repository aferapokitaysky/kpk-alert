using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class SessionControllerCoverageTests : IDisposable
{
    private readonly WatcherDbContext _db = CreateDb();

    [Fact]
    public async Task Get_Forbids_missing_local_account_claim_and_disables_caching()
    {
        SessionController controller = CreateController(_db, null);

        IActionResult result = await controller.Get(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Get_exposes_only_known_claim_roles_and_derives_capabilities_from_assignments()
    {
        var userId = Guid.NewGuid();
        Role localNotificator = new() { Id = Guid.NewGuid(), Code = "notificator", Name = "Operator" };
        Role localAuditor = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Auditor" };
        _db.Roles.AddRange(localNotificator, localAuditor);
        _db.UserRoles.AddRange(
            new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = localNotificator.Id, LocationId = Guid.NewGuid() },
            new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = localAuditor.Id, LocationId = Guid.NewGuid() });
        await _db.SaveChangesAsync();
        SessionController controller = CreateController(_db, userId,
            new Claim(ClaimTypes.Role, "sound_engineer"),
            new Claim(ClaimTypes.Role, "sound_engineer"),
            new Claim(ClaimTypes.Role, "unknown-role"));

        OkObjectResult result = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        SessionResponse response = Assert.IsType<SessionResponse>(System.Text.Json.JsonSerializer.Deserialize<SessionResponse>(
            System.Text.Json.JsonSerializer.Serialize(result.Value),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.Equal(new[] { "sound_engineer" }, response.Roles);
        Assert.True(response.Capabilities.CanOperate);
        Assert.False(response.Capabilities.CanViewAudit);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    [Fact]
    public async Task Get_allows_a_global_auditor_to_view_audit()
    {
        var userId = Guid.NewGuid();
        Role auditor = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Auditor" };
        _db.Roles.Add(auditor);
        _db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = auditor.Id });
        await _db.SaveChangesAsync();

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await CreateController(_db, userId).Get(CancellationToken.None));
        SessionResponse response = Assert.IsType<SessionResponse>(System.Text.Json.JsonSerializer.Deserialize<SessionResponse>(
            System.Text.Json.JsonSerializer.Serialize(result.Value),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.True(response.Capabilities.CanViewAudit);
        Assert.False(response.Capabilities.CanOperate);
    }

    public void Dispose() => _db.Dispose();

    private static SessionController CreateController(
        WatcherDbContext db,
        Guid? userId,
        params Claim[] additionalClaims)
    {
        List<Claim> claims = [.. additionalClaims];
        if (userId is Guid id)
        {
            claims.Add(new Claim("kgta_user_id", id.ToString()));
        }

        return new SessionController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
    }

    private static WatcherDbContext CreateDb() => new(
        new DbContextOptionsBuilder<WatcherDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private sealed record SessionResponse(bool Authenticated, string[] Roles, CapabilityResponse Capabilities);
    private sealed record CapabilityResponse(bool CanOperate, bool CanViewAudit);
}

public sealed class AuditEventsControllerCoverageTests : IDisposable
{
    private readonly WatcherDbContext _db = new(
        new DbContextOptionsBuilder<WatcherDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task List_requires_a_global_audit_role()
    {
        var userId = Guid.NewGuid();
        var locationId = Guid.NewGuid();
        Role auditor = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Auditor" };
        _db.Roles.Add(auditor);
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = auditor.Id,
            LocationId = locationId
        });
        await _db.SaveChangesAsync();
        AuditEventsController controller = CreateController(_db, userId);

        IActionResult result = await controller.List(cancellationToken: CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task List_filters_event_type_and_returns_requested_page_with_parsed_payload()
    {
        var userId = Guid.NewGuid();
        Role auditor = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Auditor" };
        _db.Roles.Add(auditor);
        _db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = auditor.Id });
        var now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        _db.AuditEvents.AddRange(
            CreateEvent("auth.login.succeeded", now.AddMinutes(-1), "{\"method\":\"google\"}"),
            CreateEvent("alert_command.created", now, "{\"commandId\":\"one\"}"),
            CreateEvent("alert_command.created", now.AddMinutes(-2), "{\"commandId\":\"two\"}"));
        await _db.SaveChangesAsync();
        AuditEventsController controller = CreateController(_db, userId);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.List(page: 2, pageSize: 1, eventType: "alert_command.created",
                cancellationToken: CancellationToken.None));
        AuditPageResponse response = Assert.IsType<AuditPageResponse>(System.Text.Json.JsonSerializer.Deserialize<AuditPageResponse>(
            System.Text.Json.JsonSerializer.Serialize(result.Value),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        Assert.Equal(2, response.TotalCount);
        Assert.Equal(2, response.Page);
        Assert.Equal(1, response.PageSize);
        Assert.Single(response.Items);
        Assert.Equal("alert_command.created", response.Items[0].EventType);
        Assert.Equal("two", response.Items[0].Payload.GetProperty("commandId").GetString());
    }

    [Theory]
    [InlineData(0, 50, null)]
    [InlineData(1, 101, null)]
    [InlineData(1, 50, " ")]
    [InlineData(1, 50, "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx")]
    public async Task List_rejects_invalid_page_or_filter_after_authorization(int page, int pageSize, string? eventType)
    {
        var userId = Guid.NewGuid();
        Role admin = new() { Id = Guid.NewGuid(), Code = "admin", Name = "Administrator" };
        _db.Roles.Add(admin);
        _db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = admin.Id });
        await _db.SaveChangesAsync();
        AuditEventsController controller = CreateController(_db, userId);

        ObjectResult result = Assert.IsType<ObjectResult>(
            await controller.List(page, pageSize, eventType, cancellationToken: CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    [Fact]
    public async Task List_rejects_offset_beyond_supported_range()
    {
        var userId = Guid.NewGuid();
        Role admin = new() { Id = Guid.NewGuid(), Code = "admin", Name = "Administrator" };
        _db.Roles.Add(admin);
        _db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = admin.Id });
        await _db.SaveChangesAsync();
        AuditEventsController controller = CreateController(_db, userId);

        ObjectResult result = Assert.IsType<ObjectResult>(
            await controller.List(page: int.MaxValue, pageSize: 100, cancellationToken: CancellationToken.None));

        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
    }

    public void Dispose() => _db.Dispose();

    private static AuditEvent CreateEvent(string eventType, DateTimeOffset occurredAtUtc, string payload) => new()
    {
        Id = Guid.NewGuid(),
        EventType = eventType,
        ActorType = "system",
        Payload = payload,
        OccurredAtUtc = occurredAtUtc
    };

    private static AuditEventsController CreateController(WatcherDbContext db, Guid userId) => new(db)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("kgta_user_id", userId.ToString())], "test"))
            }
        }
    };

    private sealed record AuditPageResponse(AuditItemResponse[] Items, int Page, int PageSize, int TotalCount);
    private sealed record AuditItemResponse(string EventType, System.Text.Json.JsonElement Payload);
}
