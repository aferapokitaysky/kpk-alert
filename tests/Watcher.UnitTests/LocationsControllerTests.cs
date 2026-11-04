using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class LocationsControllerTests : IDisposable
{
    private readonly WatcherDbContext _db;
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _firstLocationId = Guid.NewGuid();
    private readonly Guid _secondLocationId = Guid.NewGuid();

    public LocationsControllerTests()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new WatcherDbContext(options);
        _db.Locations.AddRange(
            new Location { Id = _firstLocationId, Code = "bld-a", Name = "А корпус" },
            new Location { Id = _secondLocationId, Code = "bld-b", Name = "Б корпус" });
        Role notificator = new() { Id = Guid.NewGuid(), Code = "notificator", Name = "Оператор" };
        Role soundEngineer = new() { Id = Guid.NewGuid(), Code = "sound_engineer", Name = "Звукооператор" };
        _db.Roles.AddRange(notificator, soundEngineer);
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = _userId,
            RoleId = notificator.Id,
            LocationId = _firstLocationId
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task List_returns_only_locations_assigned_to_operator()
    {
        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await CreateController(_userId).List(CancellationToken.None));

        var locations = Assert.IsAssignableFrom<IEnumerable<object>>(result.Value).ToArray();
        Assert.Single(locations);
        Assert.Contains(_firstLocationId.ToString(), System.Text.Json.JsonSerializer.Serialize(locations[0]));
    }

    [Fact]
    public async Task List_returns_every_active_location_for_global_operator()
    {
        var globalOperatorId = Guid.NewGuid();
        Role role = _db.Roles.Single(item => item.Code == "notificator");
        _db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = globalOperatorId,
            RoleId = role.Id
        });
        await _db.SaveChangesAsync();

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await CreateController(globalOperatorId).List(CancellationToken.None));

        Assert.Equal(2, Assert.IsAssignableFrom<System.Collections.IEnumerable>(result.Value).Cast<object>().Count());
    }

    [Fact]
    public async Task List_returns_empty_list_for_user_without_operator_role()
    {
        var viewerId = Guid.NewGuid();
        Role viewerRole = new() { Id = Guid.NewGuid(), Code = "auditor", Name = "Аудитор" };
        _db.Roles.Add(viewerRole);
        _db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = viewerId, RoleId = viewerRole.Id });
        await _db.SaveChangesAsync();

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await CreateController(viewerId).List(CancellationToken.None));

        Assert.Empty(Assert.IsAssignableFrom<System.Collections.IEnumerable>(result.Value).Cast<object>());
    }

    [Fact]
    public async Task List_forbids_principal_without_local_account_claim()
    {
        IActionResult result = await CreateController(null).List(CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
    }

    private LocationsController CreateController(Guid? userId)
    {
        ClaimsIdentity identity = new(userId is Guid id
            ? [new Claim("kgta_user_id", id.ToString())]
            : [], "test");
        return new LocationsController(_db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }

    public void Dispose() => _db.Dispose();
}
