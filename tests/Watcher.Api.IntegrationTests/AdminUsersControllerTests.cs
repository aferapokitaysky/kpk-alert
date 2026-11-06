using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class AdminUsersControllerTests : IClassFixture<PostgresApiFixture>
{
    private const string UsersPath = "/api/v1/admin/users";
    private readonly PostgresApiFixture _fixture;

    public AdminUsersControllerTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task User_list_is_no_store_and_available_only_to_global_administrators()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        using HttpResponseMessage administrator = await SendGetAsync(seed.AdministratorId, "admin");
        using HttpResponseMessage operatorResponse = await SendGetAsync(seed.OperatorId, "notificator");
        using HttpResponseMessage anonymous = await _fixture.Client.GetAsync(UsersPath);

        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
        Assert.Equal("no-store", administrator.Headers.CacheControl?.ToString());
        using var body = JsonDocument.Parse(await administrator.Content.ReadAsStringAsync());
        Assert.Equal(3, body.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Contains(body.RootElement.GetProperty("items").EnumerateArray(), user =>
            user.GetProperty("email").GetString() == "admin@example.test");
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
        Assert.Equal("no-store", operatorResponse.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal("no-store", anonymous.Headers.CacheControl?.ToString());

        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            UserAccount administratorAccount = await db.Users.SingleAsync(user =>
                user.Id == seed.AdministratorId);
            administratorAccount.IsActive = false;
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage deactivatedAdministrator = await SendGetAsync(seed.AdministratorId, "admin");
        Assert.Equal(HttpStatusCode.Forbidden, deactivatedAdministrator.StatusCode);
        Assert.Equal("no-store", deactivatedAdministrator.Headers.CacheControl?.ToString());
    }

    [PostgresFact]
    public async Task Role_grant_is_idempotent_audited_and_limited_to_supported_scopes()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var auditorRoleId = Guid.NewGuid();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.Roles.Add(new Role { Id = auditorRoleId, Code = "auditor", Name = "Аудитор" });
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage scopedAdmin = await SendGrantRoleAsync(
            seed.AdministratorId, seed.OperatorId, "admin", seed.FirstLocationId);
        using HttpResponseMessage firstGrant = await SendGrantRoleAsync(
            seed.AdministratorId, seed.OperatorId, "auditor", null);
        Assert.True(firstGrant.IsSuccessStatusCode, await firstGrant.Content.ReadAsStringAsync());
        using var roleBody = JsonDocument.Parse(await firstGrant.Content.ReadAsStringAsync());
        Guid assignmentId = roleBody.RootElement.GetProperty("assignmentId").GetGuid();
        using HttpResponseMessage replay = await SendGrantRoleAsync(
            seed.AdministratorId, seed.OperatorId, "auditor", null);

        Assert.Equal(HttpStatusCode.BadRequest, scopedAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.Created, firstGrant.StatusCode);
        Assert.Equal("auditor", roleBody.RootElement.GetProperty("roleCode").GetString());
        Assert.Equal("Аудитор", roleBody.RootElement.GetProperty("roleName").GetString());
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Assert.Single(db.UserRoles.Where(item => item.UserId == seed.OperatorId && item.RoleId == auditorRoleId));
            Assert.Single(db.AuditEvents.Where(item => item.EventType == "user.role_granted"));
        }

        using HttpResponseMessage revoked = await SendRevokeRoleAsync(
            seed.AdministratorId, seed.OperatorId, assignmentId);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        Assert.DoesNotContain(await verifyDb.UserRoles.ToArrayAsync(), item => item.Id == assignmentId);
        Assert.Equal(2, await verifyDb.AuditEvents.CountAsync(item => item.EventType.StartsWith("user.role_")));
    }

    [PostgresFact]
    public async Task Account_status_changes_are_audited_and_administrator_cannot_disable_self()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);
        var secondAdministratorId = Guid.NewGuid();
        var selfOperatorAssignmentId = Guid.NewGuid();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.Users.Add(new UserAccount
            {
                Id = secondAdministratorId,
                ExternalSubject = "second-admin",
                Email = "second-admin@example.test"
            });
            Role administrator = await db.Roles.SingleAsync(item => item.Code == "admin");
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = secondAdministratorId,
                RoleId = administrator.Id
            });
            Role operatorRole = await db.Roles.SingleAsync(item => item.Code == "notificator");
            db.UserRoles.Add(new UserRole
            {
                Id = selfOperatorAssignmentId,
                UserId = seed.AdministratorId,
                RoleId = operatorRole.Id,
                LocationId = seed.FirstLocationId
            });
            await db.SaveChangesAsync();
        }

        Guid selfAdministratorAssignmentId;
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Role administrator = await db.Roles.SingleAsync(item => item.Code == "admin");
            selfAdministratorAssignmentId = await db.UserRoles
                .Where(item => item.UserId == seed.AdministratorId && item.RoleId == administrator.Id)
                .Select(item => item.Id)
                .SingleAsync();
        }

        using HttpResponseMessage deactivatedSecond = await SendStatusAsync(
            seed.AdministratorId, secondAdministratorId, isActive: false);
        using HttpResponseMessage deactivatedSelf = await SendStatusAsync(
            seed.AdministratorId, seed.AdministratorId, isActive: false);
        using HttpResponseMessage revokedOwnOperator = await SendRevokeRoleAsync(
            seed.AdministratorId, seed.AdministratorId, selfOperatorAssignmentId);
        using HttpResponseMessage revokedOwnAdministrator = await SendRevokeRoleAsync(
            seed.AdministratorId, seed.AdministratorId, selfAdministratorAssignmentId);

        Assert.True(deactivatedSecond.IsSuccessStatusCode,
            await deactivatedSecond.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, deactivatedSecond.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deactivatedSelf.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, revokedOwnOperator.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, revokedOwnAdministrator.StatusCode);
        using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        Assert.False(await verifyDb.Users.Where(user => user.Id == secondAdministratorId)
            .Select(user => user.IsActive).SingleAsync());
        AuditEvent audit = await verifyDb.AuditEvents.SingleAsync(item => item.EventType == "user.status_changed");
        Assert.Equal(seed.AdministratorId, audit.ActorId);
        Assert.DoesNotContain("second-admin@example.test", audit.Payload);
    }

    private async Task<HttpResponseMessage> SendGetAsync(Guid userId, string role)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, UsersPath);
        AddIdentity(request, userId, role);
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendGrantRoleAsync(
        Guid actorId,
        Guid targetId,
        string roleCode,
        Guid? locationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{UsersPath}/{targetId}/roles")
        {
            Content = JsonContent.Create(new GrantUserRoleRequest(roleCode, locationId))
        };
        AddIdentity(request, actorId, "admin");
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(actorId));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendRevokeRoleAsync(Guid actorId, Guid targetId, Guid assignmentId)
    {
        using HttpRequestMessage request = new(
            HttpMethod.Delete, $"{UsersPath}/{targetId}/roles/{assignmentId}");
        AddIdentity(request, actorId, "admin");
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(actorId));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendStatusAsync(Guid actorId, Guid targetId, bool isActive)
    {
        using HttpRequestMessage request = new(HttpMethod.Patch, $"{UsersPath}/{targetId}/status")
        {
            Content = JsonContent.Create(new UpdateUserStatusRequest(isActive))
        };
        AddIdentity(request, actorId, "admin");
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(actorId));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<string> GetCsrfTokenAsync(Guid userId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        AddIdentity(request, userId, "admin");
        using HttpResponseMessage response = await _fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("API не повернув antiforgery token.");
    }

    private static void AddIdentity(HttpRequestMessage request, Guid userId, string? role = null)
    {
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (role is not null) request.Headers.Add("X-Test-Role", role);
    }
}
