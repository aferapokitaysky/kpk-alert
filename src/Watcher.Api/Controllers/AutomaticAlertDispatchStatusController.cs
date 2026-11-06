using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.AlertSources;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/automatic-alert-dispatch/status")]
public sealed class AutomaticAlertDispatchStatusController(
    WatcherDbContext db,
    AutomaticAlertDispatchOptions options) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var canView = await (from assignment in db.UserRoles.AsNoTracking()
                             join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                             where assignment.UserId == userId && assignment.LocationId == null &&
                                   (role.Code == "admin" || role.Code == "auditor")
                             select assignment.Id).AnyAsync(cancellationToken);
        if (!canView)
        {
            return Forbid();
        }

        AutomaticAlertDispatchState? state = await db.AutomaticAlertDispatchStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == AutomaticAlertDispatchState.KremenchukId,
                cancellationToken);
        if (state is null)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Стан автоматичного оповіщення ще не ініціалізований.");
        }

        return Ok(new
        {
            enabled = options.Enabled,
            presetCode = options.PresetCode,
            state = state.HasOpenEpisode ? "alerting" : "idle",
            state.Scope,
            state.ScopeId,
            state.StartCommandId,
            state.StartedAtUtc,
            state.LastDecisionCode,
            state.UpdatedAtUtc
        });
    }
}
