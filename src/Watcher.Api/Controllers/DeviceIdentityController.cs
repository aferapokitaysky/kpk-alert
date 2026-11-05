using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Watcher.Api.Security;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = DeviceCertificateAuthentication.Scheme)]
[Route("api/v1/device")]
public sealed class DeviceIdentityController : ControllerBase
{
    [HttpGet("identity")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult GetIdentity()
    {
        if (!Guid.TryParse(User.FindFirst(DeviceCertificateAuthentication.DeviceIdClaim)?.Value, out Guid deviceId) ||
            !Guid.TryParse(User.FindFirst(DeviceCertificateAuthentication.LocationIdClaim)?.Value, out Guid locationId))
        {
            return Unauthorized();
        }

        return Ok(new DeviceIdentityResponse(deviceId, locationId));
    }

    public sealed record DeviceIdentityResponse(Guid DeviceId, Guid LocationId);
}
