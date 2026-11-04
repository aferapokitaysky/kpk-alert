using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Route("api/v1/health")]
public sealed class HealthController(
    IConfiguration configuration,
    IServiceScopeFactory scopeFactory,
    ILogger<HealthController> logger) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok" });

    [HttpGet("live")]
    public IActionResult Live() => Ok(new { status = "ok" });

    [HttpGet("ready")]
    public async Task<IActionResult> Ready(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("Watcher")))
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { status = "unavailable", dependency = "database" });
        }

        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            if (await db.Database.CanConnectAsync(cancellationToken))
            {
                return Ok(new { status = "ok" });
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning("Перевірка готовності БД завершилася помилкою типу {ExceptionType}.",
                exception.GetType().Name);
        }

        return StatusCode(StatusCodes.Status503ServiceUnavailable,
            new { status = "unavailable", dependency = "database" });
    }
}
