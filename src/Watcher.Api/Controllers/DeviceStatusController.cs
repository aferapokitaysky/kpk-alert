using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/admin/devices")]
public sealed class DeviceStatusController(
    WatcherDbContext db,
    DeviceCommandSessionRegistry sessions) : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaximumPageSize = 100;

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        Response.Headers.CacheControl = "no-store";
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

        if (page < 1 || pageSize is < 1 or > MaximumPageSize)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте номер сторінки та її розмір.");
        }

        var offset = ((long)page - 1) * pageSize;
        if (offset > int.MaxValue)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Запитана сторінка виходить за межі списку пристроїв.");
        }

        var totalCount = await db.Devices.CountAsync(cancellationToken);
        var latestSessions = db.DeviceSessions.AsNoTracking()
            .GroupBy(item => item.DeviceId)
            .Select(group => new
            {
                DeviceId = group.Key,
                LastSeenAtUtc = group.Max(item => item.LastSeenAtUtc)
            });

        var rows = await (from device in db.Devices.AsNoTracking()
                          join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
                          join latest in latestSessions on device.Id equals latest.DeviceId into sessionsByDevice
                          from latest in sessionsByDevice.DefaultIfEmpty()
                          orderby location.Name, device.Name, device.Id
                          select new
                          {
                              device.Id,
                              device.Name,
                              LocationName = location.Name,
                              LocationIsActive = location.IsActive,
                              IsActive = device.RevokedAtUtc == null,
                              LastSeenAtUtc = latest == null ? (DateTimeOffset?)null : latest.LastSeenAtUtc
                          })
            .Skip((int)offset)
            .Take(pageSize)
            .ToArrayAsync(cancellationToken);

        var connectedDeviceIds = sessions.ConnectedDeviceIds.ToHashSet();
        // The query intentionally returns only operational fields; the certificate fingerprint stays private.
        DeviceStatusItem[] items = rows.Select(row => new DeviceStatusItem(
            row.Name,
            row.LocationName,
            row.LocationIsActive,
            row.IsActive,
            connectedDeviceIds.Contains(row.Id),
            row.LastSeenAtUtc)).ToArray();

        return Ok(new DeviceStatusPage(items, page, pageSize, totalCount));
    }
}

public sealed record DeviceStatusPage(
    IReadOnlyList<DeviceStatusItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record DeviceStatusItem(
    string Name,
    string LocationName,
    bool LocationIsActive,
    bool IsActive,
    bool IsConnected,
    DateTimeOffset? LastSeenAtUtc);
