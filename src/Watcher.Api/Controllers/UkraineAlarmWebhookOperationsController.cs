using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.AlertSources;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/admin/integrations/ukrainealarm/webhook-operations")]
public sealed class UkraineAlarmWebhookOperationsController(
    WatcherDbContext db,
    UkraineAlarmWebhookOperationsService operations) : ControllerBase
{
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartAsync(
        [FromBody] StartUkraineAlarmWebhookOperationRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        var actorUserId = Guid.Parse(User.FindFirstValue("kgta_user_id")!);
        if (idempotencyKey == Guid.Empty || request.Action is not ("register" or "update"))
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте дію та ключ повторного запиту.");
        }

        WebhookOperationResult result = await operations.StartAsync(
            actorUserId, idempotencyKey, request.Action, cancellationToken);
        if (result.Error is "configuration_invalid")
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Перевірте налаштування UkraineAlarm та публічну HTTPS-адресу.");
        }

        if (result.IsConflict)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: result.Error == "registration_state_conflict"
                    ? "Дія не відповідає відомому стану реєстрації webhook."
                    : "Інша операція webhook ще виконується або очікує ручної звірки.");
        }

        if (result.Operation is null)
        {
            return Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Не вдалося створити операцію webhook.");
        }

        if (result.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable)
        {
            return Problem(statusCode: (int)result.StatusCode.Value,
                title: "Провайдер відхилив операцію або конфігурацію. Перевірте її стан перед новою дією.",
                extensions: new Dictionary<string, object?> { ["operationId"] = result.Operation.Id });
        }

        if (result.IsReplay && result.Operation.State is "Succeeded" or "Rejected" or "Resolved")
        {
            return Ok(ToResponse(result.Operation));
        }

        return AcceptedAtRoute("GetUkraineAlarmWebhookOperation",
            new { operationId = result.Operation.Id }, ToResponse(result.Operation));
    }

    [HttpGet("state")]
    public async Task<IActionResult> GetStateAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        ExternalIntegrationState? state = await operations.GetStateAsync(cancellationToken);
        UkraineAlarmWebhookOperation? activeOperation = await operations.GetActiveAsync(cancellationToken);
        return state is null
            ? Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Стан інтеграції ще не ініціалізований.")
            : Ok(new
            {
                integrationCode = state.IntegrationCode,
                state = state.IsRegistered switch
                {
                    true => "registered",
                    false => "not_registered",
                    null => "unknown"
                },
                state.UpdatedAtUtc,
                activeOperation = activeOperation is null ? null : ToResponse(activeOperation)
            });
    }

    [HttpPost("state/reconcile")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ReconcileStateAsync(
        [FromBody] ReconcileUkraineAlarmWebhookStateRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!request.Confirmed)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Підтвердьте, що стан реєстрації перевірено в кабінеті UkraineAlarm.");
        }

        var actorUserId = Guid.Parse(User.FindFirstValue("kgta_user_id")!);
        if (!await operations.ReconcileStateAsync(actorUserId, request.IsRegistered, cancellationToken))
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Спочатку звірте поточну незавершену операцію webhook.");
        }

        return await GetStateAsync(cancellationToken);
    }

    [HttpGet("{operationId:guid}", Name = "GetUkraineAlarmWebhookOperation")]
    public async Task<IActionResult> GetAsync(Guid operationId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        UkraineAlarmWebhookOperation? operation = await operations.GetAsync(operationId, cancellationToken);
        return operation is null ? NotFound() : Ok(ToResponse(operation));
    }

    [HttpPost("{operationId:guid}/resolve")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveAsync(
        Guid operationId,
        [FromBody] ResolveUkraineAlarmWebhookOperationRequest request,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await IsGlobalAdministratorAsync(cancellationToken))
        {
            return Forbid();
        }

        if (!request.Confirmed)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Підтвердьте, що стан реєстрації перевірено в кабінеті UkraineAlarm.");
        }

        var actorUserId = Guid.Parse(User.FindFirstValue("kgta_user_id")!);
        WebhookOperationResult result = await operations.ResolveAsync(
            operationId, actorUserId, request.IsRegistered, cancellationToken);
        if (result.Operation is null)
        {
            return result.StatusCode == HttpStatusCode.NotFound
                ? NotFound()
                : Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Операцію вже завершено або її стан змінився.");
        }

        return result.IsConflict
            ? Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Операцію вже звірено з іншим станом реєстрації.")
            : Ok(ToResponse(result.Operation));
    }

    private async Task<bool> IsGlobalAdministratorAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return false;
        }

        return await (from assignment in db.UserRoles.AsNoTracking()
                      join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                      where assignment.UserId == userId && assignment.LocationId == null && role.Code == "admin"
                      select assignment.Id).AnyAsync(cancellationToken);
    }

    private static object ToResponse(UkraineAlarmWebhookOperation operation) => new
    {
        operation.Id,
        operation.Action,
        operation.State,
        operation.CallbackUrl,
        operation.OutcomeCode,
        operation.CreatedAtUtc,
        operation.UpdatedAtUtc
    };
}

public sealed record StartUkraineAlarmWebhookOperationRequest(string Action);

public sealed record ResolveUkraineAlarmWebhookOperationRequest(bool Confirmed, bool IsRegistered);

public sealed record ReconcileUkraineAlarmWebhookStateRequest(bool Confirmed, bool IsRegistered);
