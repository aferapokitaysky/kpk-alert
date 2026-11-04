using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/locations")]
public sealed class LocationsController(WatcherDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var isGlobalAdministrator = User.IsInRole("admin");
        var hasGlobalNotificatorRole = await (from assignment in db.UserRoles
                                              join role in db.Roles on assignment.RoleId equals role.Id
                                              where assignment.UserId == userId &&
                                                    assignment.LocationId == null &&
                                                    role.Code == "notificator"
                                              select assignment.Id).AnyAsync(cancellationToken);
        if (isGlobalAdministrator || hasGlobalNotificatorRole)
        {
            var allLocations = await db.Locations.AsNoTracking()
                .Where(location => location.IsActive)
                .OrderBy(location => location.Name)
                .Select(location => new { location.Id, location.Name, location.Code })
                .ToListAsync(cancellationToken);
            return Ok(allLocations);
        }

        var permittedLocations = await (from assignment in db.UserRoles
                                        join role in db.Roles on assignment.RoleId equals role.Id
                                        join location in db.Locations on assignment.LocationId equals location.Id
                                        where assignment.UserId == userId &&
                                              (role.Code == "admin" || role.Code == "notificator") &&
                                              location.IsActive
                                        select new { location.Id, location.Name, location.Code })
            .Distinct()
            .OrderBy(location => location.Name)
            .ToListAsync(cancellationToken);

        return Ok(permittedLocations);
    }
}
