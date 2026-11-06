using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Commands;
using Watcher.Api.Contracts;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/alert-commands")]
public sealed class AlertCommandsController(
    WatcherDbContext db,
    TimeProvider clock,
    ILocationCommandSequenceAllocator sequenceAllocator) : ControllerBase
{
    private const int MaximumRequestBodySizeBytes = 16 * 1024;

    [HttpPost]
    [RequestSizeLimit(MaximumRequestBodySizeBytes)]
    [RequestBodySizeLimit(MaximumRequestBodySizeBytes)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        [FromBody] CreateAlertCommandRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Потрібен коректний ключ повторного запиту.");
        }

        var accountId = User.FindFirstValue("kgta_user_id");
        if (!Guid.TryParse(accountId, out Guid userId))
        {
            return Forbid();
        }

        AlertCommand? existing = await db.AlertCommands.AsNoTracking()
            .Include(command => command.Targets)
            .SingleOrDefaultAsync(command => command.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return ReplayOrConflict(existing, userId, request);
        }

        var isStart = request.Action == "start";
        var isStop = request.Action == "stop";
        if ((!isStart && !isStop) ||
            (isStart && (string.IsNullOrWhiteSpace(request.PresetCode) ||
                         request.PresetCode.Length > 80 || request.TargetCommandId is not null)) ||
            (isStop && (request.PresetCode is not null || request.TargetCommandId is null ||
                        request.TargetCommandId == Guid.Empty)) ||
            request.LocationIds is null ||
            request.LocationIds.Count is 0 or > 50 ||
            request.LocationIds.Any(id => id == Guid.Empty) ||
            request.LocationIds.Distinct().Count() != request.LocationIds.Count)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте дію, сценарій і список корпусів.");
        }

        SoundPreset? preset = isStart
            ? await db.SoundPresets.AsNoTracking().SingleOrDefaultAsync(
                item => item.Code == request.PresetCode && item.IsPublished,
                cancellationToken)
            : null;
        if (isStart && (preset is null || !SoundPreset.HasValidManifestHash(preset.ManifestHash)))
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обраний сценарій не опублікований або має некоректний manifest hash.");
        }

        List<Location> locations = await db.Locations
            .Where(location => request.LocationIds.Contains(location.Id) && location.IsActive)
            .ToListAsync(cancellationToken);
        if (locations.Count != request.LocationIds.Count)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Один або кілька корпусів недоступні.");
        }

        List<string> roleCodes = await (from assignment in db.UserRoles
                                        join role in db.Roles on assignment.RoleId equals role.Id
                                        where assignment.UserId == userId &&
                                              (role.Code == "admin" || role.Code == "notificator")
                                        select role.Code + ":" + (assignment.LocationId.HasValue
                                            ? assignment.LocationId.Value.ToString()
                                            : "*")).ToListAsync(cancellationToken);
        if (locations.Any(location => !roleCodes.Contains("admin:*") &&
                !roleCodes.Contains("notificator:*") &&
                !roleCodes.Contains($"admin:{location.Id}") &&
                !roleCodes.Contains($"notificator:{location.Id}")))
        {
            return Forbid();
        }

        if (isStop)
        {
            AlertCommand? targetCommand = await db.AlertCommands.AsNoTracking()
                .Include(item => item.Targets)
                .SingleOrDefaultAsync(item => item.Id == request.TargetCommandId, cancellationToken);
            var requestedLocations = request.LocationIds.ToHashSet();
            if (targetCommand is null || targetCommand.Action != "start" ||
                !targetCommand.Targets.Select(target => target.LocationId).ToHashSet()
                    .IsSupersetOf(requestedLocations))
            {
                return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Команду можна зупинити лише в корпусах, для яких її було створено.");
            }

            var devices = await db.Devices.AsNoTracking()
                .Where(device => requestedLocations.Contains(device.LocationId) && device.RevokedAtUtc == null)
                .Select(device => new { device.Id, device.LocationId })
                .ToListAsync(cancellationToken);
            var deviceIds = devices.Select(device => device.Id).ToList();
            var configuredLocations = devices.Select(device => device.LocationId).ToHashSet();
            List<CommandDelivery> targetDeliveries = await db.CommandDeliveries.AsNoTracking()
                .Where(delivery => delivery.CommandId == targetCommand.Id &&
                                   deviceIds.Contains(delivery.DeviceId))
                .ToListAsync(cancellationToken);
            if (!configuredLocations.SetEquals(requestedLocations) ||
                targetDeliveries.Count != deviceIds.Count ||
                targetDeliveries.Any(delivery => delivery.State is not (
                    CommandDeliveryState.InFlight or CommandDeliveryState.Accepted or CommandDeliveryState.Playing)))
            {
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "Сигнал уже завершено або ще не передано пристрою.");
            }
        }

        DateTimeOffset createdAtUtc = clock.GetUtcNow();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            List<CommandTarget> targets = [];
            foreach (Location location in locations.OrderBy(item => item.Id))
            {
                targets.Add(new CommandTarget
                {
                    Id = Guid.NewGuid(),
                    LocationId = location.Id,
                    Sequence = await sequenceAllocator.AllocateAsync(location.Id, cancellationToken),
                    State = "Created"
                });
            }

            AlertCommand command = new()
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = idempotencyKey,
                Action = isStop ? "stop" : "start",
                PresetCode = request.PresetCode,
                TargetCommandId = request.TargetCommandId,
                MaximumPlaybackDurationSeconds = isStop ? 0 : AlertCommand.DefaultMaximumPlaybackDurationSeconds,
                InitiatedByUserId = userId,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = createdAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes),
                Targets = targets
            };

            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                command.Id,
                command.Action,
                command.PresetCode,
                command.TargetCommandId,
                command.ExpiresAtUtc,
                LocationIds = command.Targets.Select(target => target.LocationId)
            });

            db.AlertCommands.Add(command);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                CommandId = command.Id,
                EventType = "alert_command.created",
                Payload = payload,
                CreatedAtUtc = command.CreatedAtUtc
            });
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "alert_command.created",
                ActorType = "user",
                ActorId = userId,
                Payload = payload,
                OccurredAtUtc = command.CreatedAtUtc
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AcceptedAtAction(nameof(Get), new { id = command.Id }, ToResponse(command));
        }
        catch (DbUpdateException exception) when
            (exception.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_alert_commands_IdempotencyKey"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            AlertCommand? concurrentReplay = await db.AlertCommands.AsNoTracking()
                .Include(item => item.Targets)
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            return concurrentReplay is null
                ? Conflict()
                : ReplayOrConflict(concurrentReplay, userId, request);
        }

    }

    [HttpPost("{targetCommandId:guid}/stop")]
    [RequestSizeLimit(MaximumRequestBodySizeBytes)]
    [RequestBodySizeLimit(MaximumRequestBodySizeBytes)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(
        Guid targetCommandId,
        [FromBody] CreateStopCommandRequest request,
        [FromHeader(Name = "Idempotency-Key")] Guid idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (idempotencyKey == Guid.Empty)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Потрібен коректний ключ повторного запиту.");
        }

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        AlertCommand? existing = await db.AlertCommands.AsNoTracking()
            .Include(command => command.Targets)
            .SingleOrDefaultAsync(command => command.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return ReplayStopOrConflict(existing, userId, targetCommandId, request);
        }

        if (targetCommandId == Guid.Empty || request.LocationIds is not { Count: > 0 and <= 50 } locationIds ||
            locationIds.Any(id => id == Guid.Empty) || locationIds.Distinct().Count() != locationIds.Count)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте команду та список корпусів для зупинки.");
        }

        List<string> roleCodes = await (from assignment in db.UserRoles
                                        join role in db.Roles on assignment.RoleId equals role.Id
                                        where assignment.UserId == userId &&
                                              (role.Code == "admin" || role.Code == "notificator")
                                        select role.Code + ":" + (assignment.LocationId.HasValue
                                            ? assignment.LocationId.Value.ToString()
                                            : "*")).ToListAsync(cancellationToken);
        if (locationIds.Any(locationId => !roleCodes.Contains("admin:*") &&
                !roleCodes.Contains("notificator:*") &&
                !roleCodes.Contains($"admin:{locationId}") &&
                !roleCodes.Contains($"notificator:{locationId}")))
        {
            return Forbid();
        }

        AlertCommand? targetCommand = await db.AlertCommands.AsNoTracking()
            .Include(command => command.Targets)
            .SingleOrDefaultAsync(command => command.Id == targetCommandId, cancellationToken);
        if (targetCommand is null)
        {
            return NotFound();
        }

        if (targetCommand.Action != "start" ||
            locationIds.Any(locationId => targetCommand.Targets.All(target => target.LocationId != locationId)))
        {
            return NotFound();
        }

        var sourceDeliveryMaterialized = await db.OutboxMessages.AsNoTracking().AnyAsync(
            message => message.CommandId == targetCommandId && message.ProcessedAtUtc != null,
            cancellationToken);
        if (!sourceDeliveryMaterialized)
        {
            return Problem(statusCode: StatusCodes.Status409Conflict,
                title: "Доставка команди запуску ще готується. Оновіть стан і повторіть зупинку.");
        }

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Guid[] requestedLocations = locationIds.ToArray();
            List<CommandDelivery> sourceDeliveries = await db.CommandDeliveries
                .FromSqlInterpolated($"""
                    SELECT delivery.*
                    FROM command_deliveries AS delivery
                    JOIN devices AS device ON device."Id" = delivery."DeviceId"
                    WHERE delivery."CommandId" = {targetCommandId}
                      AND device."LocationId" = ANY({requestedLocations})
                    FOR UPDATE OF delivery
                    """)
                .ToListAsync(cancellationToken);
            Guid[] sourceDeviceIds = sourceDeliveries.Select(delivery => delivery.DeviceId).ToArray();
            Dictionary<Guid, Guid> sourceDeviceLocations = await db.Devices.AsNoTracking()
                .Where(device => sourceDeviceIds.Contains(device.Id))
                .ToDictionaryAsync(device => device.Id, device => device.LocationId, cancellationToken);
            var sourceLocations = sourceDeviceLocations.Values.ToHashSet();
            if (!sourceLocations.SetEquals(requestedLocations))
            {
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "У вибраних корпусах немає підготовлених доставок вихідної команди.");
            }

            HashSet<Guid> routableLocations = await (
                from device in db.Devices.AsNoTracking()
                join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
                where sourceDeviceIds.Contains(device.Id) && device.RevokedAtUtc == null && location.IsActive
                select device.LocationId)
                .ToHashSetAsync(cancellationToken);
            if (!routableLocations.SetEquals(requestedLocations))
            {
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "У кожному вибраному корпусі має бути доступний вузол для доставки команди.");
            }

            DateTimeOffset createdAtUtc = clock.GetUtcNow();
            List<CommandTarget> stopTargets = [];
            foreach (Guid locationId in requestedLocations.Order())
            {
                stopTargets.Add(new CommandTarget
                {
                    Id = Guid.NewGuid(),
                    LocationId = locationId,
                    Sequence = await sequenceAllocator.AllocateAsync(locationId, cancellationToken),
                    State = "Created"
                });
            }

            AlertCommand command = new()
            {
                Id = Guid.NewGuid(),
                IdempotencyKey = idempotencyKey,
                Action = "stop",
                TargetCommandId = targetCommandId,
                MaximumPlaybackDurationSeconds = 0,
                InitiatedByUserId = userId,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = createdAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes),
                Targets = stopTargets
            };
            db.CommandDeliveries.AddRange(sourceDeviceIds.Select(deviceId => new CommandDelivery
            {
                Id = Guid.NewGuid(),
                CommandId = command.Id,
                DeviceId = deviceId,
                State = CommandDeliveryState.Pending,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = command.ExpiresAtUtc,
                NextAttemptAtUtc = createdAtUtc
            }));
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                command.Id,
                command.Action,
                command.TargetCommandId,
                command.ExpiresAtUtc,
                LocationIds = stopTargets.Select(target => target.LocationId)
            });

            db.AlertCommands.Add(command);
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                CommandId = command.Id,
                EventType = "alert_command.created",
                Payload = payload,
                CreatedAtUtc = createdAtUtc
            });
            db.AuditEvents.Add(new AuditEvent
            {
                Id = Guid.NewGuid(),
                EventType = "alert_command.stop_requested",
                ActorType = "user",
                ActorId = userId,
                Payload = payload,
                OccurredAtUtc = createdAtUtc
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return AcceptedAtAction(nameof(Get), new { id = command.Id }, ToResponse(command));
        }
        catch (DbUpdateException exception) when
            (exception.InnerException is Npgsql.PostgresException
            {
                SqlState: Npgsql.PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_alert_commands_IdempotencyKey"
            })
        {
            await transaction.RollbackAsync(cancellationToken);
            AlertCommand? concurrentReplay = await db.AlertCommands.AsNoTracking()
                .Include(item => item.Targets)
                .SingleOrDefaultAsync(item => item.IdempotencyKey == idempotencyKey, cancellationToken);
            return concurrentReplay is null
                ? Conflict()
                : ReplayStopOrConflict(concurrentReplay, userId, targetCommandId, request);
        }
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return NotFound();
        }

        IQueryable<AlertCommand> query = db.AlertCommands.AsNoTracking()
            .Where(item => item.Id == id);
        if (!User.IsInRole("admin") && !User.IsInRole("auditor"))
        {
            query = query.Where(item => item.InitiatedByUserId == userId);
        }

        AlertCommand? command = await query
            .Include(item => item.Targets)
            .SingleOrDefaultAsync(cancellationToken);
        if (command is null)
        {
            return NotFound();
        }

        return Ok(ToResponse(command));
    }

    private static object ToResponse(AlertCommand command) => new
    {
        command.Id,
        command.Action,
        command.PresetCode,
        command.TargetCommandId,
        command.CreatedAtUtc,
        command.ExpiresAtUtc,
        Targets = command.Targets.Select(target => new { target.LocationId, target.State })
    };

    private static bool Matches(AlertCommand command, CreateAlertCommandRequest request)
    {
        if (request.LocationIds is not { } locationIds ||
            !string.Equals(command.Action, request.Action, StringComparison.Ordinal) ||
            !string.Equals(command.PresetCode, request.PresetCode, StringComparison.Ordinal) ||
            command.TargetCommandId != request.TargetCommandId ||
            command.Targets.Count != locationIds.Count)
        {
            return false;
        }

        var existingLocationIds = command.Targets.Select(target => target.LocationId).ToHashSet();
        return existingLocationIds.SetEquals(locationIds);
    }

    private IActionResult ReplayOrConflict(
        AlertCommand existing,
        Guid userId,
        CreateAlertCommandRequest request)
    {
        if (existing.InitiatedByUserId != userId || !Matches(existing, request))
        {
            return Conflict();
        }

        return AcceptedAtAction(nameof(Get), new { id = existing.Id }, ToResponse(existing));
    }

    private IActionResult ReplayStopOrConflict(
        AlertCommand existing,
        Guid userId,
        Guid targetCommandId,
        CreateStopCommandRequest request)
    {
        if (existing.InitiatedByUserId != userId ||
            existing.Action != "stop" || existing.TargetCommandId != targetCommandId ||
            request.LocationIds is not { } locationIds ||
            existing.Targets.Count != locationIds.Count ||
            !existing.Targets.Select(target => target.LocationId).ToHashSet().SetEquals(locationIds))
        {
            return Conflict();
        }

        return AcceptedAtAction(nameof(Get), new { id = existing.Id }, ToResponse(existing));
    }
}
