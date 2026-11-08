using System.Net;
using System.Net.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.AlertSources;

public sealed class UkraineAlarmWebhookOperationsService(
    WatcherDbContext db,
    UkraineAlarmWebhookRegistrationService registration,
    TimeProvider clock)
{
    public async Task<WebhookOperationResult> StartAsync(
        Guid actorUserId,
        Guid idempotencyKey,
        string action,
        CancellationToken cancellationToken)
    {
        UkraineAlarmWebhookOperation? replay = await db.UkraineAlarmWebhookOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return SameRequest(replay, actorUserId, action)
                ? new(replay, IsReplay: true, IsConflict: false)
                : WebhookOperationResult.Conflict();
        }

        Uri callbackUri;
        try
        {
            callbackUri = registration.GetCallbackUrl();
        }
        catch (InvalidOperationException)
        {
            return WebhookOperationResult.ConfigurationError();
        }

        DateTimeOffset now = clock.GetUtcNow();
        var operation = new UkraineAlarmWebhookOperation
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            Action = action,
            State = "Pending",
            CallbackUrl = callbackUri.AbsoluteUri,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await using (IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            ExternalIntegrationState? integrationState = await LockIntegrationStateAsync(cancellationToken);
            if (integrationState is null)
            {
                throw new InvalidOperationException("Стан UkraineAlarm webhook не ініціалізований міграцією.");
            }

            UkraineAlarmWebhookOperation? concurrentReplay = await db.UkraineAlarmWebhookOperations.AsNoTracking()
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            if (concurrentReplay is not null)
            {
                return SameRequest(concurrentReplay, actorUserId, action)
                    ? new(concurrentReplay, IsReplay: true, IsConflict: false)
                    : WebhookOperationResult.Conflict();
            }

            if (await db.UkraineAlarmWebhookOperations.AnyAsync(item =>
                    new[] { "Pending", "Running", "Unknown" }.Contains(item.State), cancellationToken))
            {
                return WebhookOperationResult.Conflict();
            }

            if ((action == "register" && integrationState.IsRegistered != false) ||
                (action == "update" && integrationState.IsRegistered != true))
            {
                return WebhookOperationResult.InvalidTransition();
            }

            db.UkraineAlarmWebhookOperations.Add(operation);
            db.AuditEvents.Add(CreateAuditEvent(
                "integration.ukrainealarm_webhook.requested", actorUserId, operation, "requested", now));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (IsUniqueViolation(exception))
            {
                await transaction.RollbackAsync(cancellationToken);
                db.ChangeTracker.Clear();
                UkraineAlarmWebhookOperation? duplicate = await db.UkraineAlarmWebhookOperations.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
                return duplicate is not null && SameRequest(duplicate, actorUserId, action)
                    ? new(duplicate, IsReplay: true, IsConflict: false)
                    : WebhookOperationResult.Conflict();
            }
        }

        operation.State = "Running";
        operation.UpdatedAtUtc = clock.GetUtcNow();
        db.AuditEvents.Add(CreateAuditEvent(
            "integration.ukrainealarm_webhook.running", actorUserId, operation, "provider_request_started",
            operation.UpdatedAtUtc));
        await db.SaveChangesAsync(CancellationToken.None);

        try
        {
            if (action == "register")
            {
                await registration.RegisterAsync(callbackUri, cancellationToken);
            }
            else
            {
                await registration.UpdateAsync(callbackUri, cancellationToken);
            }

            await CompleteAsync(operation, "Succeeded", "provider_accepted", actorUserId, registered: true);
        }
        catch (UkraineAlarmApiException exception) when ((int)exception.StatusCode is >= 400 and < 500)
        {
            await CompleteAsync(operation, "Rejected", "provider_rejected", actorUserId);
            return new(operation, IsReplay: false, IsConflict: false, HttpStatusCode.BadGateway);
        }
        catch (UkraineAlarmApiException)
        {
            await CompleteAsync(operation, "Unknown", "upstream_indeterminate", actorUserId);
            return new(operation, IsReplay: false, IsConflict: false, HttpStatusCode.Accepted);
        }
        catch (HttpRequestException)
        {
            await CompleteAsync(operation, "Unknown", "transport_indeterminate", actorUserId);
            return new(operation, IsReplay: false, IsConflict: false, HttpStatusCode.Accepted);
        }
        catch (OperationCanceledException)
        {
            await CompleteAsync(operation, "Unknown", "transport_indeterminate", actorUserId);
            return new(operation, IsReplay: false, IsConflict: false, HttpStatusCode.Accepted);
        }
        catch (InvalidOperationException)
        {
            await CompleteAsync(operation, "Rejected", "configuration_invalid", actorUserId);
            return new(operation, IsReplay: false, IsConflict: false, HttpStatusCode.ServiceUnavailable);
        }

        return new(operation, IsReplay: false, IsConflict: false, HttpStatusCode.OK);
    }

    public async Task<WebhookOperationResult> ResolveAsync(
        Guid operationId,
        Guid actorUserId,
        bool isRegistered,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        ExternalIntegrationState? integrationState = await LockIntegrationStateAsync(cancellationToken);
        if (integrationState is null)
        {
            throw new InvalidOperationException("Стан UkraineAlarm webhook не ініціалізований міграцією.");
        }

        UkraineAlarmWebhookOperation? operation = await db.UkraineAlarmWebhookOperations
            .FromSqlInterpolated($"SELECT * FROM ukrainealarm_webhook_operations WHERE \"Id\" = {operationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (operation is null)
        {
            return WebhookOperationResult.NotFound();
        }

        if (operation.State == "Resolved")
        {
            return operation.OutcomeCode == (isRegistered ? "manually_confirmed_registered" : "manually_confirmed_absent")
                ? new(operation, IsReplay: true, IsConflict: false)
                : WebhookOperationResult.Conflict();
        }

        if (operation.State != "Unknown")
        {
            return WebhookOperationResult.Conflict();
        }

        DateTimeOffset now = clock.GetUtcNow();
        operation.State = "Resolved";
        operation.OutcomeCode = isRegistered ? "manually_confirmed_registered" : "manually_confirmed_absent";
        operation.UpdatedAtUtc = now;
        integrationState.IsRegistered = isRegistered;
        integrationState.UpdatedAtUtc = now;
        db.AuditEvents.Add(CreateAuditEvent(
            "integration.ukrainealarm_webhook.resolved", actorUserId, operation, operation.OutcomeCode, now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(operation, IsReplay: false, IsConflict: false);
    }

    public async Task<UkraineAlarmWebhookOperation?> GetAsync(Guid operationId, CancellationToken cancellationToken) =>
        await db.UkraineAlarmWebhookOperations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == operationId, cancellationToken);

    public Task<UkraineAlarmWebhookOperation?> GetActiveAsync(CancellationToken cancellationToken) =>
        db.UkraineAlarmWebhookOperations.AsNoTracking()
            .Where(item => new[] { "Pending", "Running", "Unknown" }.Contains(item.State))
            .OrderBy(item => item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ExternalIntegrationState?> GetStateAsync(CancellationToken cancellationToken) =>
        db.ExternalIntegrationStates.AsNoTracking()
            .SingleOrDefaultAsync(item => item.IntegrationCode == UkraineAlarmWebhookOperation.DefaultIntegrationCode,
                cancellationToken);

    public async Task<bool> ReconcileStateAsync(
        Guid actorUserId,
        bool isRegistered,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        ExternalIntegrationState? integrationState = await LockIntegrationStateAsync(cancellationToken);
        if (integrationState is null)
        {
            throw new InvalidOperationException("Стан UkraineAlarm webhook не ініціалізований міграцією.");
        }

        if (await db.UkraineAlarmWebhookOperations.AnyAsync(item =>
                new[] { "Pending", "Running", "Unknown" }.Contains(item.State), cancellationToken))
        {
            return false;
        }

        DateTimeOffset now = clock.GetUtcNow();
        integrationState.IsRegistered = isRegistered;
        integrationState.UpdatedAtUtc = now;
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "integration.ukrainealarm_webhook.state_reconciled",
            ActorType = "user",
            ActorId = actorUserId,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                integrationCode = integrationState.IntegrationCode,
                isRegistered,
                outcome = "operator_confirmed"
            }),
            OccurredAtUtc = now
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private async Task CompleteAsync(
        UkraineAlarmWebhookOperation operation,
        string state,
        string outcomeCode,
        Guid actorUserId,
        bool? registered = null)
    {
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
        ExternalIntegrationState? integrationState = await LockIntegrationStateAsync(CancellationToken.None);
        if (integrationState is null)
        {
            throw new InvalidOperationException("Стан UkraineAlarm webhook не ініціалізований міграцією.");
        }

        await db.Entry(operation).ReloadAsync(CancellationToken.None);
        if (operation.State != "Running")
        {
            await transaction.CommitAsync(CancellationToken.None);
            return;
        }

        DateTimeOffset now = clock.GetUtcNow();
        operation.State = state;
        operation.OutcomeCode = outcomeCode;
        operation.UpdatedAtUtc = now;
        if (registered.HasValue)
        {
            integrationState.IsRegistered = registered.Value;
            integrationState.UpdatedAtUtc = now;
        }

        db.AuditEvents.Add(CreateAuditEvent(
            $"integration.ukrainealarm_webhook.{state.ToLowerInvariant()}", actorUserId, operation, outcomeCode, now));
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    private Task<ExternalIntegrationState?> LockIntegrationStateAsync(CancellationToken cancellationToken) =>
        db.ExternalIntegrationStates
            .FromSqlInterpolated($"SELECT * FROM external_integration_states WHERE \"IntegrationCode\" = {UkraineAlarmWebhookOperation.DefaultIntegrationCode} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static AuditEvent CreateAuditEvent(
        string eventType,
        Guid actorUserId,
        UkraineAlarmWebhookOperation operation,
        string outcome,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorType = "user",
            ActorId = actorUserId,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                operationId = operation.Id,
                operation.Action,
                operation.CallbackUrl,
                outcome
            }),
            OccurredAtUtc = now
        };

    private static bool SameRequest(UkraineAlarmWebhookOperation operation, Guid actorUserId, string action) =>
        operation.ActorUserId == actorUserId && operation.Action == action;

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is Npgsql.PostgresException
        {
            SqlState: Npgsql.PostgresErrorCodes.UniqueViolation
        };
}

public sealed record WebhookOperationResult(
    UkraineAlarmWebhookOperation? Operation,
    bool IsReplay,
    bool IsConflict,
    HttpStatusCode? StatusCode = null,
    string? Error = null)
{
    public static WebhookOperationResult Conflict() => new(null, false, true, HttpStatusCode.Conflict);
    public static WebhookOperationResult InvalidTransition() =>
        new(null, false, true, HttpStatusCode.Conflict, "registration_state_conflict");
    public static WebhookOperationResult ConfigurationError() =>
        new(null, false, false, HttpStatusCode.ServiceUnavailable, "configuration_invalid");
    public static WebhookOperationResult NotFound() => new(null, false, false, HttpStatusCode.NotFound);
}
