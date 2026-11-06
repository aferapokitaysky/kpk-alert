using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/audit-events")]
public sealed class AuditEventsController(WatcherDbContext db) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        [FromQuery] string? eventType = null,
        [FromQuery] Guid? actorId = null,
        [FromQuery] DateTimeOffset? fromUtc = null,
        [FromQuery] DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var canReadAudit = await (from assignment in db.UserRoles
                                  join role in db.Roles on assignment.RoleId equals role.Id
                                  where assignment.UserId == userId && assignment.LocationId == null &&
                                        (role.Code == "auditor" || role.Code == "admin")
                                  select assignment.Id).AnyAsync(cancellationToken);
        if (!canReadAudit)
        {
            return Forbid();
        }

        if (page < 1 || pageSize is < 1 or > MaximumPageSize ||
            (eventType is not null && (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 100)) ||
            fromUtc.HasValue != toUtc.HasValue ||
            (fromUtc is DateTimeOffset start && start.Offset != TimeSpan.Zero) ||
            (toUtc is DateTimeOffset end && end.Offset != TimeSpan.Zero) ||
            (fromUtc is DateTimeOffset rangeStart && toUtc is DateTimeOffset rangeEnd &&
             (rangeStart >= rangeEnd || rangeEnd - rangeStart > TimeSpan.FromDays(90))))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте сторінку, тип події, ідентифікатор ініціатора й UTC-діапазон до 90 днів.");
        }

        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Запитана сторінка виходить за межі журналу.");
        }

        IQueryable<AuditEvent> query = db.AuditEvents.AsNoTracking();
        if (eventType is not null)
        {
            query = query.Where(item => item.EventType == eventType);
        }
        if (actorId is Guid selectedActorId)
        {
            query = query.Where(item => item.ActorId == selectedActorId);
        }
        if (fromUtc is DateTimeOffset selectedStart && toUtc is DateTimeOffset selectedEnd)
        {
            query = query.Where(item => item.OccurredAtUtc >= selectedStart && item.OccurredAtUtc < selectedEnd);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        List<AuditEvent> events = await query
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenByDescending(item => item.Id)
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        var items = events.Select(item => new
        {
            item.Id,
            item.EventType,
            item.ActorType,
            item.ActorId,
            item.OccurredAtUtc,
            Payload = ParsePayload(item.Payload)
        });

        return Ok(new { items, page, pageSize, totalCount });
    }

    private static JsonElement ParsePayload(string payload)
    {
        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }
}
