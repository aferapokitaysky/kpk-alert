using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Contracts;
using Watcher.Api.Delivery;
using Watcher.Api.Security;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/device-status")]
public sealed class DeviceConnectionStatusController(
    WatcherDbContext db,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var isActiveUser = await db.Users.AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.IsActive, cancellationToken);
        if (!isActiveUser)
        {
            return Forbid();
        }

        var isGlobalAdministrator = await (from assignment in db.UserRoles.AsNoTracking()
                                           join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                           where assignment.UserId == userId &&
                                                 assignment.LocationId == null && role.Code == "admin"
                                           select assignment.Id).AnyAsync(cancellationToken);
        if (User.IsInRole("admin") && !isGlobalAdministrator)
        {
            var hasLocationAdministratorRole = await (from assignment in db.UserRoles.AsNoTracking()
                                                      join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                                      where assignment.UserId == userId &&
                                                            assignment.LocationId != null && role.Code == "admin"
                                                      select assignment.Id).AnyAsync(cancellationToken);
            if (!hasLocationAdministratorRole)
            {
                return Forbid();
            }
        }

        var hasGlobalNotificatorRole = await (from assignment in db.UserRoles.AsNoTracking()
                                              join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                              where assignment.UserId == userId &&
                                                    assignment.LocationId == null &&
                                                    role.Code == "notificator"
                                              select assignment.Id).AnyAsync(cancellationToken);

        List<Guid> authorizedLocationIds = [];
        if (!isGlobalAdministrator && !hasGlobalNotificatorRole)
        {
            authorizedLocationIds = await (from assignment in db.UserRoles.AsNoTracking()
                                           join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                           join location in db.Locations.AsNoTracking()
                                               on assignment.LocationId equals location.Id
                                           where assignment.UserId == userId &&
                                                 (role.Code == "admin" || role.Code == "notificator") &&
                                                 location.IsActive
                                           select location.Id)
                .Distinct()
                .ToListAsync(cancellationToken);

            if (authorizedLocationIds.Count == 0)
            {
                return Ok(Array.Empty<DeviceConnectionStatusResponse>());
            }
        }

        List<DeviceConnectionStatusResponse> devices = await (
            from device in db.Devices.AsNoTracking()
            join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
            where device.RevokedAtUtc == null &&
                  location.IsActive &&
                  (isGlobalAdministrator || hasGlobalNotificatorRole ||
                   authorizedLocationIds.Contains(location.Id))
            orderby location.Name, device.Name, device.Id
            select new DeviceConnectionStatusResponse(
                device.Id,
                device.Name,
                location.Id,
                location.Name,
                "offline"))
            .ToListAsync(cancellationToken);

        Guid[] deviceIds = devices.Select(device => device.DeviceId).ToArray();
        HashSet<Guid> connectedDeviceIds = await DeviceSessionPresenceService
            .ConnectedDeviceIds(db, timeProvider.GetUtcNow())
            .Where(deviceId => deviceIds.Contains(deviceId))
            .ToHashSetAsync(cancellationToken);
        DeviceConnectionStatusResponse[] response = devices
            .Select(device => device with
            {
                ConnectionState = connectedDeviceIds.Contains(device.DeviceId) ? "connected" : "offline"
            })
            .ToArray();

        return Ok(response);
    }
}
