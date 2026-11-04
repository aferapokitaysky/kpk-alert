using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/presets")]
public sealed class PresetsController(WatcherDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var isGlobalAdministrator = User.IsInRole("admin");
        var canNotify = await (from assignment in db.UserRoles
                               join role in db.Roles on assignment.RoleId equals role.Id
                               where assignment.UserId == userId &&
                                     (role.Code == "admin" || role.Code == "notificator")
                               select assignment.Id).AnyAsync(cancellationToken);
        if (!isGlobalAdministrator && !canNotify)
        {
            return Forbid();
        }

        var presets = await db.SoundPresets.AsNoTracking()
            .Where(preset => preset.IsPublished)
            .OrderBy(preset => preset.Priority)
            .ThenBy(preset => preset.Name)
            .Select(preset => new { preset.Code, preset.Name })
            .ToListAsync(cancellationToken);

        return Ok(presets);
    }
}
