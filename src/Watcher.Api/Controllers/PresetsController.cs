using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Watcher.Api.Contracts;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize]
[NoStoreResponse]
[Route("api/v1/presets")]
public sealed class PresetsController(WatcherDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return Forbid();
        }

        var canAccessPresets = await (from assignment in db.UserRoles.AsNoTracking()
                                      join account in db.Users.AsNoTracking() on assignment.UserId equals account.Id
                                      join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                      where assignment.UserId == userId && account.IsActive &&
                                            (role.Code == "admin" || role.Code == "notificator")
                                      select assignment.Id).AnyAsync(cancellationToken);
        if (!canAccessPresets)
        {
            return Forbid();
        }

        var presets = await db.SoundPresets.AsNoTracking()
            .Where(preset => preset.IsPublished)
            .OrderBy(preset => preset.Priority)
            .ThenBy(preset => preset.Name)
            .Select(preset => new { preset.Code, preset.Name })
            .ToListAsync(cancellationToken);

        return Ok(presets);
    }

    [HttpGet("manage")]
    public async Task<IActionResult> Manage(CancellationToken cancellationToken)
    {
        if (await GetGlobalAdministratorIdAsync(cancellationToken) is null)
        {
            return Forbid();
        }

        List<SoundPresetResponse> presets = await db.SoundPresets.AsNoTracking()
            .OrderBy(preset => preset.Priority)
            .ThenBy(preset => preset.Name)
            .Select(preset => ToResponse(preset))
            .ToListAsync(cancellationToken);

        return Ok(presets);
    }

    [HttpGet("{code}")]
    public async Task<IActionResult> GetManagedPreset(string code, CancellationToken cancellationToken)
    {
        if (await GetGlobalAdministratorIdAsync(cancellationToken) is null)
        {
            return Forbid();
        }

        if (!IsValidCode(code))
        {
            return ValidationProblem("Код сценарію має некоректний формат.");
        }

        SoundPreset? preset = await db.SoundPresets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Code == code, cancellationToken);

        return preset is null ? NotFound() : Ok(ToResponse(preset));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        CreateSoundPresetRequest request,
        CancellationToken cancellationToken)
    {
        if (await GetGlobalAdministratorIdAsync(cancellationToken) is not Guid administratorId)
        {
            return Forbid();
        }

        var code = request.Code.Trim();
        var name = request.Name.Trim();
        if (!IsValidCode(code) || !IsValidName(name) ||
            !SoundPreset.HasValidManifestHash(request.ManifestHash))
        {
            return ValidationProblem("Код, назва або SHA-256 маніфесту мають некоректний формат.");
        }

        if (await db.SoundPresets.AnyAsync(preset => preset.Code == code, cancellationToken))
        {
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Сценарій із таким кодом уже існує." });
        }

        SoundPreset preset = new()
        {
            Id = Guid.NewGuid(),
            Code = code,
            Name = name,
            Priority = request.Priority,
            ManifestHash = request.ManifestHash,
            IsPublished = false
        };
        db.SoundPresets.Add(preset);
        AddAuditEvent(administratorId, "sound_preset.created", preset);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            return Conflict(new ProblemDetails { Status = StatusCodes.Status409Conflict, Title = "Сценарій із таким кодом уже існує." });
        }

        return CreatedAtAction(nameof(GetManagedPreset), new { code = preset.Code }, ToResponse(preset));
    }

    [HttpPut("{code}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Update(
        string code,
        UpdateSoundPresetRequest request,
        CancellationToken cancellationToken)
    {
        if (await GetGlobalAdministratorIdAsync(cancellationToken) is not Guid administratorId)
        {
            return Forbid();
        }

        if (!IsValidCode(code) || !IsValidName(request.Name.Trim()) ||
            !SoundPreset.HasValidManifestHash(request.ManifestHash))
        {
            return ValidationProblem("Назва або SHA-256 маніфесту мають некоректний формат.");
        }

        SoundPreset? preset = await db.SoundPresets
            .SingleOrDefaultAsync(item => item.Code == code, cancellationToken);
        if (preset is null)
        {
            return NotFound();
        }

        if (preset.IsPublished)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Зніміть публікацію сценарію перед зміною його метаданих."
            });
        }

        preset.Name = request.Name.Trim();
        preset.Priority = request.Priority;
        preset.ManifestHash = request.ManifestHash;
        AddAuditEvent(administratorId, "sound_preset.updated", preset);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Сценарій змінено паралельно. Оновіть дані й повторіть дію."
            });
        }

        return Ok(ToResponse(preset));
    }

    [HttpPut("{code}/publication")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPublication(
        string code,
        SetSoundPresetPublicationRequest request,
        CancellationToken cancellationToken)
    {
        if (await GetGlobalAdministratorIdAsync(cancellationToken) is not Guid administratorId)
        {
            return Forbid();
        }

        if (!IsValidCode(code))
        {
            return ValidationProblem("Код сценарію має некоректний формат.");
        }

        SoundPreset? preset = await db.SoundPresets
            .SingleOrDefaultAsync(item => item.Code == code, cancellationToken);
        if (preset is null)
        {
            return NotFound();
        }

        var isPublished = request.IsPublished!.Value;
        if (preset.IsPublished != isPublished)
        {
            preset.IsPublished = isPublished;
            AddAuditEvent(administratorId, "sound_preset.publication_changed", preset);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return Conflict(new ProblemDetails
                {
                    Status = StatusCodes.Status409Conflict,
                    Title = "Сценарій змінено паралельно. Оновіть дані й повторіть дію."
                });
            }
        }

        return Ok(ToResponse(preset));
    }

    private async Task<Guid?> GetGlobalAdministratorIdAsync(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue("kgta_user_id"), out Guid userId))
        {
            return null;
        }

        var isAdministrator = await (from assignment in db.UserRoles.AsNoTracking()
                                     join role in db.Roles.AsNoTracking() on assignment.RoleId equals role.Id
                                     join account in db.Users.AsNoTracking() on assignment.UserId equals account.Id
                                     where assignment.UserId == userId &&
                                           account.IsActive &&
                                           assignment.LocationId == null &&
                                           role.Code == "admin"
                                     select assignment.Id).AnyAsync(cancellationToken);
        if (!isAdministrator)
        {
            return null;
        }

        return userId;
    }

    private void AddAuditEvent(Guid administratorId, string eventType, SoundPreset preset)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorType = "user",
            ActorId = administratorId,
            Payload = JsonSerializer.Serialize(new
            {
                presetId = preset.Id,
                preset.Code,
                preset.Name,
                preset.Priority,
                manifestHash = preset.ManifestHash,
                preset.IsPublished
            }),
            OccurredAtUtc = DateTimeOffset.UtcNow
        });
    }

    private static bool IsValidCode(string code) =>
        code.Length is > 0 and <= 80 && code.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static bool IsValidName(string name) => name.Length is > 0 and <= 160;

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static SoundPresetResponse ToResponse(SoundPreset preset) => new(
        preset.Code,
        preset.Name,
        preset.Priority,
        preset.ManifestHash,
        preset.IsPublished);
}
