using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Domain.Scheduling;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
[Route("api/v1/minute-of-silence/history")]
public sealed class MinuteOfSilenceHistoryController(WatcherDbContext db, TimeProvider clock) : ControllerBase
{
    private const int MaximumPageSize = 100;
    private const int MaximumRangeDays = 90;

    [HttpGet]
    public async Task<IActionResult> ListAsync(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var canViewHistory = await (from assignment in db.UserRoles.AsNoTracking()
                                    join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                    where assignment.UserId == userId && assignment.LocationId == null &&
                                          (role.Code == "admin" || role.Code == "auditor")
                                    select assignment.Id).AnyAsync(cancellationToken);
        if (!canViewHistory)
        {
            return Forbid();
        }

        if (from is null || to is null || page < 1 || pageSize is < 1 or > MaximumPageSize)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Вкажіть коректний період, номер сторінки та розмір сторінки.");
        }

        DateOnly startDate = from.Value;
        DateOnly endDate = to.Value;
        var kyivTimeZone = TimeZoneInfo.FindSystemTimeZoneById(MinuteOfSilenceSchedule.TimeZoneId);
        DateOnly todayInKyiv = MinuteOfSilenceSchedule.GetLocalDate(clock.GetUtcNow(), kyivTimeZone);

        if (startDate > endDate || endDate > todayInKyiv ||
            endDate.DayNumber - startDate.DayNumber >= MaximumRangeDays)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Період має охоплювати не більше 90 днів і завершуватися сьогодні або раніше.");
        }

        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Запитана сторінка виходить за межі журналу.");
        }

        IQueryable<MinuteOfSilenceOccurrence> query = db.MinuteOfSilenceOccurrences.AsNoTracking()
            .Where(item => item.RunDate >= startDate && item.RunDate <= endDate);
        var totalCount = await query.CountAsync(cancellationToken);
        MinuteOfSilenceHistoryEntry[] items = await query
            .OrderByDescending(item => item.RunDate)
            .Skip((int)offset)
            .Take(pageSize)
            .Select(item => new MinuteOfSilenceHistoryEntry(
                item.RunDate,
                item.Outcome,
                item.SkipReason,
                item.ProcessedAtUtc))
            .ToArrayAsync(cancellationToken);

        return Ok(new MinuteOfSilenceHistoryPage(items, from.Value, to.Value, page, pageSize, totalCount));
    }
}

public sealed record MinuteOfSilenceHistoryEntry(
    DateOnly RunDate,
    string Outcome,
    string? SkipReason,
    DateTimeOffset ProcessedAtUtc);

public sealed record MinuteOfSilenceHistoryPage(
    IReadOnlyList<MinuteOfSilenceHistoryEntry> Items,
    DateOnly From,
    DateOnly To,
    int Page,
    int PageSize,
    int TotalCount);
