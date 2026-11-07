using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Security;
using Watcher.Api.Users;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(WatcherDbContext db, TimeProvider timeProvider) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ManagedRoleCodes =
        ["admin", "auditor", "notificator", "sound_engineer"];

    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (page < 1 || pageSize is < 1 or > 100 || search?.Length > 100)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте номер сторінки, розмір і пошуковий запит.");
        }

        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Запитана сторінка виходить за межі списку облікових записів.");
        }

        IQueryable<UserAccount> usersQuery = db.Users.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var emailPart = search.Trim();
            usersQuery = usersQuery.Where(user => EF.Functions.ILike(user.Email, $"%{emailPart}%"));
        }

        var totalCount = await usersQuery.CountAsync(cancellationToken);
        UserAccount[] users = await usersQuery
            .OrderBy(user => user.Email)
            .ThenBy(user => user.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);
        Guid[] userIds = users.Select(user => user.Id).ToArray();
        var assignments = await (from assignment in db.UserRoles.AsNoTracking()
                                 join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                 join location in db.Locations.AsNoTracking()
                                     on assignment.LocationId equals location.Id into locations
                                 from location in locations.DefaultIfEmpty()
                                 where userIds.Contains(assignment.UserId)
                                 orderby role.Name, location!.Name
                                 select new
                                 {
                                     assignment.Id,
                                     assignment.UserId,
                                     RoleCode = role.Code,
                                     RoleName = role.Name,
                                     assignment.LocationId,
                                     LocationName = location == null ? null : location.Name
                                 }).ToArrayAsync(cancellationToken);

        var roleAssignmentsByUser = assignments
            .GroupBy(item => item.UserId)
            .ToDictionary(group => group.Key, group => group.Select(item => new UserRoleResponse(
                item.Id, item.RoleCode, item.RoleName, item.LocationId, item.LocationName)).ToArray());
        return Ok(new UserPageResponse(
            users.Select(user => new UserResponse(
                user.Id,
                user.Email,
                user.IsActive,
                roleAssignmentsByUser.GetValueOrDefault(user.Id, []))).ToArray(),
            page,
            pageSize,
            totalCount));
    }

    [HttpGet("role-catalog")]
    public async Task<IActionResult> GetRoleCatalogAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        RoleCatalogEntry[] roles = await db.Roles.AsNoTracking()
            .Where(role => ManagedRoleCodes.Contains(role.Code))
            .OrderBy(role => role.Name)
            .Select(role => new RoleCatalogEntry(
                role.Code,
                role.Name,
                role.Code == "admin" || role.Code == "auditor",
                role.Code == "notificator" || role.Code == "sound_engineer"))
            .ToArrayAsync(cancellationToken);
        return Ok(roles);
    }

    [HttpPatch("{userId:guid}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateStatusAsync(
        Guid userId,
        [FromBody] UpdateUserStatusRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAccessMutationsAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        UserAccount? user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        if (user.IsActive == request.IsActive)
        {
            await transaction.CommitAsync(cancellationToken);
            return Ok(new { userId, isActive = user.IsActive });
        }

        var isGlobalAdministrator = await IsGlobalAdministratorAccountAsync(userId, cancellationToken);
        var activeGlobalAdministrators = isGlobalAdministrator
            ? await CountOtherActiveGlobalAdministratorsAsync(userId, cancellationToken)
            : 0;
        UserAccessMutationDecision decision = UserAccessMutationPolicy.Evaluate(
            GetActorUserId(),
            userId,
            deactivatesAccount: !request.IsActive,
            removesGlobalAdministrator: !request.IsActive && isGlobalAdministrator,
            remainingActiveGlobalAdministrators: activeGlobalAdministrators);
        if (!decision.Allowed)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: decision.RejectionCode == "last_global_administrator"
                    ? "У системі має залишитися щонайменше один активний глобальний адміністратор."
                    : "Адміністратор не може деактивувати власний обліковий запис.");
        }

        var previousIsActive = user.IsActive;
        user.IsActive = request.IsActive;
        AddAuditEvent("user.status_changed", new
        {
            targetUserId = user.Id,
            previousIsActive,
            isActive = user.IsActive
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Ok(new { userId, isActive = user.IsActive });
    }

    [HttpPost("{userId:guid}/roles")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GrantRoleAsync(
        Guid userId,
        [FromBody] GrantUserRoleRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!ManagedRoleCodes.Contains(request.RoleCode))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Обрана роль не підтримується.");
        }

        if (request.LocationId is not null && (request.RoleCode is "admin" or "auditor"))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Ролі адміністратора й аудитора призначаються лише глобально.");
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAccessMutationsAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        UserAccount? user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        Role? role = await db.Roles.SingleOrDefaultAsync(item => item.Code == request.RoleCode,
            cancellationToken);
        if (role is null)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обраної ролі ще немає в довіднику системи.");
        }

        if (request.LocationId is Guid locationId &&
            !await db.Locations.AnyAsync(location => location.Id == locationId && location.IsActive,
                cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обраний корпус не існує або неактивний.");
        }
        var locationName = request.LocationId is Guid selectedLocationId
            ? await db.Locations.AsNoTracking()
                .Where(location => location.Id == selectedLocationId)
                .Select(location => location.Name)
                .SingleAsync(cancellationToken)
            : null;

        UserRole? existing = await db.UserRoles.SingleOrDefaultAsync(assignment =>
            assignment.UserId == userId && assignment.RoleId == role.Id &&
            assignment.LocationId == request.LocationId, cancellationToken);
        if (existing is not null)
        {
            await transaction.CommitAsync(cancellationToken);
            return Ok(ToRoleResponse(existing, role.Code, role.Name, locationName));
        }

        UserRole assignment = new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            LocationId = request.LocationId
        };
        db.UserRoles.Add(assignment);
        AddAuditEvent("user.role_granted", new
        {
            targetUserId = userId,
            roleCode = role.Code,
            locationId = assignment.LocationId
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            ToRoleResponse(assignment, role.Code, role.Name, locationName));
    }

    [HttpDelete("{userId:guid}/roles/{assignmentId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeRoleAsync(
        Guid userId,
        Guid assignmentId,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockUserAccessMutationsAsync(cancellationToken);
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        UserAccount? user = await db.Users
            .FromSqlInterpolated($"SELECT * FROM users WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        UserRole? assignment = await db.UserRoles.SingleOrDefaultAsync(item =>
            item.Id == assignmentId && item.UserId == userId, cancellationToken);
        if (assignment is null)
        {
            return NotFound();
        }

        Role role = await db.Roles.SingleAsync(item => item.Id == assignment.RoleId, cancellationToken);
        var removesActiveGlobalAdministrator = role.Code == "admin" && assignment.LocationId is null && user.IsActive;
        var activeGlobalAdministrators = removesActiveGlobalAdministrator
            ? await CountOtherActiveGlobalAdministratorsAsync(userId, cancellationToken)
            : 0;
        UserAccessMutationDecision decision = UserAccessMutationPolicy.Evaluate(
            GetActorUserId(),
            userId,
            deactivatesAccount: false,
            removesGlobalAdministrator: removesActiveGlobalAdministrator,
            remainingActiveGlobalAdministrators: activeGlobalAdministrators);
        if (!decision.Allowed)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: decision.RejectionCode == "last_global_administrator"
                    ? "У системі має залишитися щонайменше один активний глобальний адміністратор."
                    : "Адміністратор не може зняти власну роль адміністратора.");
        }

        db.UserRoles.Remove(assignment);
        AddAuditEvent("user.role_revoked", new
        {
            targetUserId = userId,
            roleCode = role.Code,
            locationId = assignment.LocationId
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return NoContent();
    }

    private async Task<bool> IsGlobalAdministratorAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return false;
        }

        return await (from user in db.Users.AsNoTracking()
                      join assignment in db.UserRoles.AsNoTracking() on user.Id equals assignment.UserId
                      join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                      where user.Id == userId && user.IsActive && assignment.LocationId == null &&
                            role.Code == "admin"
                      select assignment.Id).AnyAsync(cancellationToken);
    }

    private async Task<bool> IsGlobalAdministratorAccountAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await (from assignment in db.UserRoles.AsNoTracking()
               join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
               where assignment.UserId == userId && assignment.LocationId == null && role.Code == "admin"
               select assignment.Id).AnyAsync(cancellationToken);

    private async Task<int> CountOtherActiveGlobalAdministratorsAsync(
        Guid excludedUserId,
        CancellationToken cancellationToken) =>
        await (from assignment in db.UserRoles.AsNoTracking()
               join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
               join user in db.Users.AsNoTracking() on assignment.UserId equals user.Id
               where assignment.UserId != excludedUserId && assignment.LocationId == null &&
                     role.Code == "admin" && user.IsActive
               select user.Id).Distinct().CountAsync(cancellationToken);

    private async Task LockUserAccessMutationsAsync(CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(721934106)", cancellationToken);

    private Guid GetActorUserId() => Guid.Parse(User.FindFirstValue("kgta_user_id")!);

    private void AddAuditEvent(string eventType, object payload) => db.AuditEvents.Add(new AuditEvent
    {
        Id = Guid.NewGuid(),
        EventType = eventType,
        ActorType = "user",
        ActorId = GetActorUserId(),
        Payload = JsonSerializer.Serialize(payload, JsonOptions),
        OccurredAtUtc = timeProvider.GetUtcNow()
    });

    private static UserRoleResponse ToRoleResponse(
        UserRole assignment,
        string roleCode,
        string roleName,
        string? locationName) =>
        new(assignment.Id, roleCode, roleName, assignment.LocationId, locationName);
}

public sealed record UpdateUserStatusRequest(bool IsActive);
public sealed record GrantUserRoleRequest(string RoleCode, Guid? LocationId);
public sealed record UserPageResponse(IReadOnlyList<UserResponse> Items, int Page, int PageSize, int TotalCount);
public sealed record UserResponse(Guid Id, string Email, bool IsActive, IReadOnlyList<UserRoleResponse> Roles);
public sealed record UserRoleResponse(
    Guid AssignmentId,
    string RoleCode,
    string RoleName,
    Guid? LocationId,
    string? LocationName);
public sealed record RoleCatalogEntry(string Code, string Name, bool GlobalOnly, bool SupportsLocationScope);
