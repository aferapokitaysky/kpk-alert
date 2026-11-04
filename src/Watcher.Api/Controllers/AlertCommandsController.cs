using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Api.Contracts;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/alert-commands")]
public sealed class AlertCommandsController(WatcherDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpPost]
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
            if (existing.InitiatedByUserId != userId)
            {
                return Conflict();
            }

            if (!Matches(existing, request))
            {
                return Conflict();
            }

            return AcceptedAtAction(nameof(Get), new { id = existing.Id }, ToResponse(existing));
        }

        if (request.Action != "start" || string.IsNullOrWhiteSpace(request.PresetCode) ||
            request.PresetCode.Length > 80 || request.LocationIds is null ||
            request.LocationIds.Count is 0 or > 50 ||
            request.LocationIds.Any(id => id == Guid.Empty) ||
            request.LocationIds.Distinct().Count() != request.LocationIds.Count)
        {
            return Problem(statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте дію, сценарій і список корпусів.");
        }

        var presetExists = await db.SoundPresets.AnyAsync(
            preset => preset.Code == request.PresetCode && preset.IsPublished,
            cancellationToken);
        if (!presetExists)
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Обраний сценарій недоступний.");
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

        AlertCommand command = new()
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            Action = request.Action,
            PresetCode = request.PresetCode,
            InitiatedByUserId = userId,
            CreatedAtUtc = clock.GetUtcNow(),
            Targets = locations.Select(location => new CommandTarget
            {
                Id = Guid.NewGuid(),
                LocationId = location.Id,
                State = "Created"
            }).ToList()
        };

        db.AlertCommands.Add(command);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            CommandId = command.Id,
            EventType = "alert_command.created",
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                command.Id,
                command.Action,
                command.PresetCode,
                LocationIds = command.Targets.Select(target => target.LocationId)
            }),
            CreatedAtUtc = command.CreatedAtUtc
        });
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = "alert_command.created",
            ActorType = "user",
            ActorId = userId,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                command.Id,
                command.Action,
                command.PresetCode,
                LocationIds = command.Targets.Select(target => target.LocationId)
            }),
            OccurredAtUtc = command.CreatedAtUtc
        });

        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when
            (exception.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            return Conflict();
        }

        return AcceptedAtAction(nameof(Get), new { id = command.Id }, ToResponse(command));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        AlertCommand? command = await db.AlertCommands.AsNoTracking()
            .Include(item => item.Targets)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (command is null)
        {
            return NotFound();
        }

        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId) ||
            (command.InitiatedByUserId != userId && !User.IsInRole("admin") && !User.IsInRole("auditor")))
        {
            return Forbid();
        }

        return Ok(ToResponse(command));
    }

    private static object ToResponse(AlertCommand command) => new
    {
        command.Id,
        command.Action,
        command.PresetCode,
        command.CreatedAtUtc,
        Targets = command.Targets.Select(target => new { target.LocationId, target.State })
    };

    private static bool Matches(AlertCommand command, CreateAlertCommandRequest request)
    {
        if (request.LocationIds is not { } locationIds ||
            !string.Equals(command.Action, request.Action, StringComparison.Ordinal) ||
            !string.Equals(command.PresetCode, request.PresetCode, StringComparison.Ordinal) ||
            command.Targets.Count != locationIds.Count)
        {
            return false;
        }

        var existingLocationIds = command.Targets.Select(target => target.LocationId).ToHashSet();
        return existingLocationIds.SetEquals(locationIds);
    }
}
