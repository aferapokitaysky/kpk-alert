using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Watcher.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController(IConfiguration configuration) : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("csrf-token")]
    public IActionResult CsrfToken([FromServices] IAntiforgery antiforgery)
    {
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
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync();
        return NoContent();
    }
}
