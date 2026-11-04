using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
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
        CancellationToken cancellationToken = default)
    {
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
            (eventType is not null && (string.IsNullOrWhiteSpace(eventType) || eventType.Length > 100)))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте номер сторінки, її розмір і тип події.");
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
