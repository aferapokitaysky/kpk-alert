using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/auth/session")]
public sealed class SessionController(WatcherDbContext db) : ControllerBase
{
    private static readonly HashSet<string> ExposedRoles = new(StringComparer.Ordinal)
    {
        "admin",
        "auditor",
        "notificator",
        "sound_engineer"
    };

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var assignedRoles = await (from assignment in db.UserRoles.AsNoTracking()
                                   join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                   where assignment.UserId == userId
                                   select new { role.Code, IsGlobal = assignment.LocationId == null })
            .ToListAsync(cancellationToken);

        var roles = User.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(ExposedRoles.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var canOperate = roles.Contains("admin", StringComparer.Ordinal) ||
                         roles.Contains("notificator", StringComparer.Ordinal) ||
                         assignedRoles.Any(assignment => assignment.Code is "admin" or "notificator");
        var canViewAudit = assignedRoles.Any(assignment => assignment.IsGlobal &&
            assignment.Code is "admin" or "auditor");

        return Ok(new
        {
            authenticated = true,
            roles,
            capabilities = new { canOperate, canViewAudit }
        });
    }
}
