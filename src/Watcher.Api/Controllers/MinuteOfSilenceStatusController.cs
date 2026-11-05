using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/minute-of-silence/status/today")]
public sealed class MinuteOfSilenceStatusController(WatcherDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetToday(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var canViewStatus = await (from assignment in db.UserRoles.AsNoTracking()
                                   join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                   where assignment.UserId == userId && assignment.LocationId == null &&
                                         (role.Code == "admin" || role.Code == "auditor")
                                   select assignment.Id).AnyAsync(cancellationToken);
        if (!canViewStatus)
        {
            return Forbid();
        }

        var kyivTimeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateTimeOffset nowUtc = clock.GetUtcNow();
        DateOnly runDate = MinuteOfSilenceSchedule.GetLocalDate(nowUtc, kyivTimeZone);
        DateTimeOffset scheduledAtUtc = MinuteOfSilenceSchedule.GetOccurrenceUtc(runDate, kyivTimeZone);
        MinuteOfSilenceOccurrence? occurrence = await db.MinuteOfSilenceOccurrences.AsNoTracking()
            .SingleOrDefaultAsync(item => item.RunDate == runDate, cancellationToken);

        return Ok(new
        {
            runDate,
            timeZone = MinuteOfSilenceSchedule.TimeZoneId,
            scheduledAtUtc,
            outcome = occurrence?.Outcome,
            occurrence?.SkipReason,
            occurrence?.CommandId,
            occurrence?.ProcessedAtUtc
        });
    }
}
