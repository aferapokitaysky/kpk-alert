using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Watcher.Api.AlertSources;

namespace Watcher.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/alert-sources")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class AlertSourcesController(AlertSourcesStatusService statusService) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<AlertSourcesStatusResponse>> GetStatusAsync(
        CancellationToken cancellationToken)
    {
        AlertSourcesStatusResponse status = await statusService.GetStatusAsync(cancellationToken);
        return Ok(status);
    }
}
