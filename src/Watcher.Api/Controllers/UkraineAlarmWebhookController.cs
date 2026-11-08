using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Watcher.Api.AlertSources;

namespace Watcher.Api.Controllers;

[ApiController]
[AllowAnonymous]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/v1/alert-sources/ukrainealarm/webhook")]
public sealed class UkraineAlarmWebhookController(
    IUkraineAlarmRefreshSignal refreshSignal,
    AlertSourcesStatusService statusService,
    TimeProvider timeProvider,
    ILogger<UkraineAlarmWebhookController> logger) : ControllerBase
{
    private const int MaximumBodyLength = 8 * 1024;
    private static readonly EventId RefreshQueuedEvent = new(4200, "UkraineAlarmRefreshQueued");
    private static readonly Meter Meter = new("Watcher.Api.AlertSources");
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("ukrainealarm.webhook.requests");

    [HttpPost]
    [RequestSizeLimit(MaximumBodyLength)]
    public async Task<IActionResult> ReceiveAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (!refreshSignal.TryAdmit(HttpContext.Connection.RemoteIpAddress, now, out TimeSpan retryAfter))
        {
            Requests.Add(1, new KeyValuePair<string, object?>("result", "rate_limited"));
            Response.Headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
            return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        if (Request.ContentLength > MaximumBodyLength)
        {
            Requests.Add(1, new KeyValuePair<string, object?>("result", "too_large"));
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        var buffer = new byte[4096];
        var totalBytes = 0;
        while (true)
        {
            var bytesRead = await Request.Body.ReadAsync(buffer, cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            totalBytes += bytesRead;
            if (totalBytes > MaximumBodyLength)
            {
                Requests.Add(1, new KeyValuePair<string, object?>("result", "too_large"));
                return StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        }

        var refreshQueued = refreshSignal.TrySignal(now);
        statusService.InvalidateCache();
        Requests.Add(1, new KeyValuePair<string, object?>("result", "accepted"));
        if (refreshQueued)
        {
            logger.LogInformation(
                RefreshQueuedEvent,
                "Прийнято callback UkraineAlarm; запитано позачергове оновлення стану.");
        }

        return Accepted();
    }
}
