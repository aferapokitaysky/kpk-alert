using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/alert-commands/{commandId:guid}/deliveries")]
public sealed class CommandDeliveriesController(WatcherDbContext db) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;

    [HttpGet]
    public async Task<IActionResult> List(
        Guid commandId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return NotFound();
        }

        IQueryable<AlertCommand> commandQuery = db.AlertCommands.AsNoTracking()
            .Where(item => item.Id == commandId);
        if (!User.IsInRole("admin") && !User.IsInRole("auditor"))
        {
            commandQuery = commandQuery.Where(item => item.InitiatedByUserId == userId);
        }

        if (!await commandQuery.AnyAsync(cancellationToken))
        {
            return NotFound();
        }

        if (page < 1 || pageSize is < 1 or > MaximumPageSize)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте номер сторінки та її розмір.");
        }

        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Запитана сторінка виходить за межі списку доставок.");
        }

        IQueryable<CommandDelivery> deliveriesQuery = db.CommandDeliveries.AsNoTracking()
            .Where(delivery => delivery.CommandId == commandId);
        Dictionary<CommandDeliveryState, int> stateCounts = await deliveriesQuery
            .GroupBy(delivery => delivery.State)
            .Select(group => new { State = group.Key, Count = group.Count() })
            .ToDictionaryAsync(item => item.State, item => item.Count, cancellationToken);
        int Count(CommandDeliveryState state) => stateCounts.GetValueOrDefault(state);
        var totalCount = stateCounts.Values.Sum();
        var hasUnfinishedDeliveries =
            Count(CommandDeliveryState.Pending) +
            Count(CommandDeliveryState.InFlight) +
            Count(CommandDeliveryState.Accepted) +
            Count(CommandDeliveryState.Playing) > 0;
        var stateSummary = new
        {
            pending = Count(CommandDeliveryState.Pending),
            inFlight = Count(CommandDeliveryState.InFlight),
            accepted = Count(CommandDeliveryState.Accepted),
            playing = Count(CommandDeliveryState.Playing),
            completed = Count(CommandDeliveryState.Completed),
            stopped = Count(CommandDeliveryState.Stopped),
            rejected = Count(CommandDeliveryState.Rejected),
            failed = Count(CommandDeliveryState.Failed),
            expired = Count(CommandDeliveryState.Expired)
        };
        var items = await (
            from delivery in deliveriesQuery
            join device in db.Devices.AsNoTracking() on delivery.DeviceId equals device.Id
            orderby delivery.DeviceId
            select new
            {
                DeviceId = device.Id,
                DeviceName = device.Name,
                State = delivery.State.ToString(),
                delivery.CreatedAtUtc,
                delivery.ExpiresAtUtc,
                delivery.LastAttemptAtUtc,
                delivery.AttemptCount
            })
            .Skip((int)offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return Ok(new { items, page, pageSize, totalCount, hasUnfinishedDeliveries, stateSummary });
    }
}
