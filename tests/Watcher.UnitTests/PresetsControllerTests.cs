using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class PresetsControllerTests : IDisposable
{
    private readonly WatcherDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _notificatorRoleId = Guid.NewGuid();

    public PresetsControllerTests()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new WatcherDbContext(options);
        _db.Users.Add(new UserAccount
        {
            Id = _userId,
            ExternalSubject = $"preset-controller-test-{_userId}",
            Email = $"{_userId}@example.test",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        _db.Roles.Add(new Role { Id = _notificatorRoleId, Code = "notificator", Name = "Оператор" });
        _db.SoundPresets.AddRange(
            new SoundPreset
            {
                Id = Guid.NewGuid(),
                Code = "evacuation",
                Name = "Евакуація",
                Priority = 1,
                ManifestHash = new string('a', 64),
                IsPublished = true
            },
            new SoundPreset
            {
                Id = Guid.NewGuid(),
                Code = "draft",
                Name = "Чернетка",
                Priority = 0,
                ManifestHash = new string('b', 64),
                IsPublished = false
            });
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            RoleId = _notificatorRoleId,
            LocationId = Guid.NewGuid()
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task List_returns_only_published_presets_for_location_operator()
    {
        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await CreateController(_userId).List(CancellationToken.None));

        var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
        Assert.Contains("evacuation", json);
        Assert.DoesNotContain("draft", json);
    }

    [Fact]
    public async Task List_forbids_user_without_operator_role()
    {
        IActionResult result = await CreateController(Guid.NewGuid()).List(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    [Fact]
    public async Task List_forbids_principal_without_local_account_claim()
    {
        IActionResult result = await CreateController(null).List(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    private PresetsController CreateController(Guid? userId)
    {
        ClaimsIdentity identity = new(userId is Guid id
            ? [new Claim("kgta_user_id", id.ToString())]
            : [], "test");
        return new PresetsController(_db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    public void Dispose() => _db.Dispose();
}
