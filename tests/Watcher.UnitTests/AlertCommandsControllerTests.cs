using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Watcher.Api.Commands;
using Watcher.Api.Contracts;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class AlertCommandsControllerTests : IDisposable
{
    private readonly WatcherDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _locationId = Guid.NewGuid();
    private readonly Guid _presetId = Guid.NewGuid();
    private readonly Guid _roleId = Guid.NewGuid();
    private readonly Guid _idempotencyKey = Guid.NewGuid();
    private readonly InMemoryLocationCommandSequenceAllocator _sequenceAllocator = new();

    public AlertCommandsControllerTests()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new WatcherDbContext(options);
        _db.Users.Add(new UserAccount
        {
            Id = _userId,
            ExternalSubject = "google-subject",
            Email = "operator@example.test"
        });
        _db.Locations.Add(new Location { Id = _locationId, Code = "bld-1", Name = "Головний корпус" });
        _db.SoundPresets.Add(new SoundPreset
        {
            Id = _presetId,
            Code = "evacuation",
            Name = "Евакуація",
            Priority = 1,
            ManifestHash = new string('a', 64),
            IsPublished = true
        });
        _db.Roles.Add(new Role { Id = _roleId, Code = "notificator", Name = "Оператор" });
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            RoleId = _roleId,
            LocationId = _locationId
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Create_saves_command_target_and_audit_together()
    {
        IActionResult result = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(result);
        AlertCommand command = Assert.Single(_db.AlertCommands);
        Assert.Equal(command.CreatedAtUtc.AddMinutes(AlertCommand.DefaultValidityMinutes), command.ExpiresAtUtc);
        Assert.Single(_db.CommandTargets);
        Assert.Equal(1, _db.CommandTargets.Single().Sequence);
        OutboxMessage outbox = Assert.Single(_db.OutboxMessages);
        Assert.Equal(command.Id, outbox.CommandId);
        Assert.Equal("alert_command.created", outbox.EventType);
        Assert.Contains(_locationId.ToString(), outbox.Payload);
        using var outboxPayload = JsonDocument.Parse(outbox.Payload);
        Assert.Equal(command.ExpiresAtUtc,
            outboxPayload.RootElement.GetProperty("ExpiresAtUtc").GetDateTimeOffset());
        Assert.Null(outbox.ProcessedAtUtc);
        Assert.Equal(0, outbox.Attempts);
        AuditEvent audit = Assert.Single(_db.AuditEvents);
        Assert.Equal("alert_command.created", audit.EventType);
        Assert.Equal(_userId, audit.ActorId);
        Assert.Contains(_locationId.ToString(), audit.Payload);
    }

    [Fact]
    public async Task Create_allocates_a_new_sequence_for_each_command_target()
    {
        IActionResult first = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);
        IActionResult second = await CreateController(_userId).Create(
            ValidRequest(), Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(first);
        Assert.IsType<AcceptedAtActionResult>(second);
        Assert.Equal([1L, 2L], _db.CommandTargets
            .OrderBy(target => target.Sequence)
            .Select(target => target.Sequence));
    }

    [Theory]
    [InlineData("stop", "evacuation")]
    [InlineData("start", " ")]
    public async Task Create_rejects_unsupported_or_empty_fields(string action, string preset)
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest(action, preset, [_locationId]),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
        Assert.Empty(_db.AlertCommands);
        Assert.Empty(_db.AuditEvents);
    }

    [Fact]
    public async Task Create_rejects_missing_idempotency_key()
    {
        IActionResult result = await CreateController(_userId).Create(
            ValidRequest(), Guid.Empty, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_rejects_empty_location_list()
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", []),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_rejects_empty_location_identifier()
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", [Guid.Empty]),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
        Assert.Empty(_db.AlertCommands);
    }

    [Fact]
    public async Task Create_rejects_a_preset_code_over_the_storage_limit()
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", new string('a', 81), [_locationId]),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
        Assert.Empty(_db.AlertCommands);
    }

    [Fact]
    public async Task Create_rejects_more_than_fifty_locations()
    {
        Guid[] locationIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray();

        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", locationIds),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
        Assert.Empty(_db.AlertCommands);
    }

    [Fact]
    public async Task Create_accepts_exactly_fifty_authorized_locations()
    {
        var additionalLocations = Enumerable.Range(0, 49).Select(index => new Location
        {
            Id = Guid.NewGuid(),
            Code = $"bld-{index + 2}",
            Name = $"Корпус {index + 2}"
        }).ToList();
        _db.Locations.AddRange(additionalLocations);
        _db.UserRoles.AddRange(additionalLocations.Select(location => new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            RoleId = _roleId,
            LocationId = location.Id
        }));
        await _db.SaveChangesAsync();
        Guid[] locationIds = [_locationId, .. additionalLocations.Select(location => location.Id)];

        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", locationIds),
            _idempotencyKey, CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(result);
        Assert.Equal(50, _db.CommandTargets.Count());
    }

    [Fact]
    public async Task Create_rejects_an_unknown_location()
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", [Guid.NewGuid()]),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status422UnprocessableEntity);
        Assert.Empty(_db.AlertCommands);
    }

    [Fact]
    public async Task Create_rejects_duplicate_locations()
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", [_locationId, _locationId]),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_rejects_null_locations_from_malformed_json()
    {
        IActionResult result = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", null),
            _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Create_rejects_unpublished_preset()
    {
        _db.SoundPresets.Single().IsPublished = false;
        await _db.SaveChangesAsync();

        IActionResult result = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status422UnprocessableEntity);
        Assert.Empty(_db.AlertCommands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-sha256-hash")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public async Task Create_rejects_published_preset_with_invalid_manifest_hash(string manifestHash)
    {
        _db.SoundPresets.Single().ManifestHash = manifestHash;
        await _db.SaveChangesAsync();

        IActionResult result = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status422UnprocessableEntity);
        Assert.Empty(_db.AlertCommands);
        Assert.Empty(_db.OutboxMessages);
    }

    [Fact]
    public async Task Create_rejects_inactive_location()
    {
        _db.Locations.Single().IsActive = false;
        await _db.SaveChangesAsync();

        IActionResult result = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        AssertProblem(result, StatusCodes.Status422UnprocessableEntity);
    }

    [Fact]
    public async Task Create_rejects_user_without_location_permission()
    {
        IActionResult result = await CreateController(Guid.NewGuid()).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(_db.AlertCommands);
    }

    [Fact]
    public async Task Create_allows_global_administrator_role()
    {
        var adminUserId = Guid.NewGuid();
        var adminRoleId = Guid.NewGuid();
        _db.Users.Add(new UserAccount
        {
            Id = adminUserId,
            ExternalSubject = "admin-subject",
            Email = "admin@example.test"
        });
        _db.Roles.Add(new Role { Id = adminRoleId, Code = "admin", Name = "Адміністратор" });
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = adminUserId,
            RoleId = adminRoleId
        });
        await _db.SaveChangesAsync();

        IActionResult result = await CreateController(adminUserId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(result);
    }

    [Fact]
    public async Task Create_returns_existing_command_for_same_user_and_key()
    {
        IActionResult first = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);
        IActionResult second = await CreateController(_userId).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(first);
        Assert.IsType<AcceptedAtActionResult>(second);
        Assert.Single(_db.AlertCommands);
        Assert.Single(_db.AuditEvents);
        Assert.Single(_db.OutboxMessages);
    }

    [Fact]
    public async Task Create_returns_conflict_when_same_key_is_reused_with_different_payload()
    {
        await CreateController(_userId).Create(ValidRequest(), _idempotencyKey, CancellationToken.None);
        CreateAlertCommandRequest[] changedRequests =
        [
            new("stop", "evacuation", [_locationId]),
            new("start", "other-preset", [_locationId]),
            new("start", "evacuation", [Guid.NewGuid()])
        ];

        foreach (CreateAlertCommandRequest request in changedRequests)
        {
            IActionResult result = await CreateController(_userId).Create(
                request, _idempotencyKey, CancellationToken.None);

            Assert.IsType<ConflictResult>(result);
        }

        Assert.Single(_db.AlertCommands);
        Assert.Single(_db.CommandTargets);
        Assert.Single(_db.AuditEvents);
        Assert.Single(_db.OutboxMessages);
    }

    [Fact]
    public async Task Create_treats_reordered_location_ids_as_the_same_payload()
    {
        Location secondLocation = new() { Id = Guid.NewGuid(), Code = "bld-2", Name = "Другий корпус" };
        _db.Locations.Add(secondLocation);
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            RoleId = _roleId,
            LocationId = secondLocation.Id
        });
        await _db.SaveChangesAsync();
        Guid[] locationIds = [_locationId, secondLocation.Id];

        IActionResult first = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", locationIds),
            _idempotencyKey, CancellationToken.None);
        IActionResult replay = await CreateController(_userId).Create(
            new CreateAlertCommandRequest("start", "evacuation", locationIds.Reverse().ToArray()),
            _idempotencyKey, CancellationToken.None);

        Assert.IsType<AcceptedAtActionResult>(first);
        Assert.IsType<AcceptedAtActionResult>(replay);
        Assert.Single(_db.AlertCommands);
        Assert.Equal(2, _db.CommandTargets.Count());
        Assert.Single(_db.AuditEvents);
        Assert.Single(_db.OutboxMessages);
    }

    [Fact]
    public async Task Create_does_not_reveal_another_users_idempotency_result()
    {
        await CreateController(_userId).Create(ValidRequest(), _idempotencyKey, CancellationToken.None);

        IActionResult result = await CreateController(Guid.NewGuid()).Create(
            ValidRequest(), _idempotencyKey, CancellationToken.None);

        Assert.IsType<ConflictResult>(result);
        Assert.Single(_db.AlertCommands);
        Assert.Single(_db.OutboxMessages);
    }

    [Fact]
    public async Task Get_returns_not_found_for_unknown_command()
    {
        IActionResult result = await CreateController(_userId).Get(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_hides_another_users_command()
    {
        await CreateController(_userId).Create(ValidRequest(), _idempotencyKey, CancellationToken.None);
        Guid commandId = _db.AlertCommands.Single().Id;

        IActionResult result = await CreateController(Guid.NewGuid()).Get(commandId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Get_allows_an_auditor_to_read_another_users_command()
    {
        await CreateController(_userId).Create(ValidRequest(), _idempotencyKey, CancellationToken.None);
        Guid commandId = _db.AlertCommands.Single().Id;

        IActionResult result = await CreateController(Guid.NewGuid(), "auditor")
            .Get(commandId, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
    }

    private AlertCommandsController CreateController(Guid userId, params string[] roles)
    {
        Claim[] claims = [new Claim("kgta_user_id", userId.ToString()), .. roles.Select(role =>
            new Claim(ClaimTypes.Role, role))];
        AlertCommandsController controller = new(_db, TimeProvider.System, _sequenceAllocator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        claims, "test"))
                }
            }
        };
        return controller;
    }

    private CreateAlertCommandRequest ValidRequest() =>
        new("start", "evacuation", [_locationId]);

    private sealed class InMemoryLocationCommandSequenceAllocator : ILocationCommandSequenceAllocator
    {
        private readonly Dictionary<Guid, long> _nextByLocation = [];

        public Task<long> AllocateAsync(Guid locationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sequence = _nextByLocation.GetValueOrDefault(locationId) + 1;
            _nextByLocation[locationId] = sequence;
            return Task.FromResult(sequence);
        }
    }

    private static void AssertProblem(IActionResult result, int expectedStatus)
    {
        ObjectResult problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(expectedStatus, problem.StatusCode);
        Assert.IsType<ProblemDetails>(problem.Value);
    }

    public void Dispose() => _db.Dispose();
}
