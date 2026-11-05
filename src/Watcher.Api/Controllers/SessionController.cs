using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/auth/session")]
public sealed class SessionController : ControllerBase
{
    private static readonly HashSet<string> ExposedRoles = new(StringComparer.Ordinal)
    {
        "admin",
        "auditor",
        "notificator",
        "sound_engineer"
    };

    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out _))
        {
            return Forbid();
        }

        var roles = User.FindAll(ClaimTypes.Role)
            .Select(claim => claim.Value)
            .Where(ExposedRoles.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        return Ok(new { authenticated = true, roles });
    }
}
