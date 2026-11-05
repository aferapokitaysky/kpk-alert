using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("csrf-token")]
    public IActionResult CsrfToken([FromServices] IAntiforgery antiforgery)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(new { token = tokens.RequestToken });
    }

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = "/")
    {
        if (!configuration.GetValue<bool>("Authentication:Google:Enabled"))
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Вхід тимчасово не налаштований.");
        }

        var safeReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        AuthenticationProperties properties = new() { RedirectUri = safeReturnUrl };
        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    [Authorize]
    [ValidateAntiForgeryToken]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout(
        [FromServices] WatcherDbContext db,
        [FromServices] TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        await AuthenticationAuditWriter.RecordAsync(
            db, clock, "auth.logout", userId, "session", cancellationToken: cancellationToken);
        await HttpContext.SignOutAsync();
        return NoContent();
    }
}
