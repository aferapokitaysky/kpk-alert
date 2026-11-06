using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Commands;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class AlertCommandResourceAuthorizationTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task Get_hides_existing_command_when_account_claim_is_missing_or_malformed(
        string? accountClaim)
    {
        var ownerId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        await using WatcherDbContext db = new(
            new DbContextOptionsBuilder<WatcherDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        db.AlertCommands.Add(new AlertCommand
        {
            Id = commandId,
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            InitiatedByUserId = ownerId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(AlertCommand.DefaultValidityMinutes)
        });
        await db.SaveChangesAsync();

        Claim[] claims = accountClaim is null ? [] : [new Claim("kgta_user_id", accountClaim)];
        AlertCommandsController controller = new(db, TimeProvider.System, new UnusedSequenceAllocator())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };

        IActionResult result = await controller.Get(commandId, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Equal("no-store", controller.Response.Headers.CacheControl);
    }

    private sealed class UnusedSequenceAllocator : ILocationCommandSequenceAllocator
    {
        public Task<long> AllocateAsync(Guid locationId, CancellationToken cancellationToken) =>
            Task.FromResult(1L);
    }
}
