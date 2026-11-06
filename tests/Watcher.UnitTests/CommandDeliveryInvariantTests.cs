using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Controllers;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class CommandDeliveryInvariantTests
{
    [Theory]
    [InlineData(CommandDeliveryState.Pending)]
    [InlineData(CommandDeliveryState.InFlight)]
    [InlineData(CommandDeliveryState.Accepted)]
    [InlineData(CommandDeliveryState.Playing)]
    public async Task List_reports_active_deliveries_as_unfinished(CommandDeliveryState state)
    {
        await using WatcherDbContext db = CreateDbContext();
        (Guid commandId, Guid userId) = await SeedAsync(db, [state]);
        CommandDeliveriesController controller = CreateController(db, userId);

        IActionResult result = await controller.List(commandId, cancellationToken: CancellationToken.None);

        using JsonDocument response = ReadResponse(result);
        Assert.True(response.RootElement.GetProperty("hasUnfinishedDeliveries").GetBoolean());
        Assert.Equal(1, response.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement delivery = Assert.Single(response.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal(state.ToString(), delivery.GetProperty("state").GetString());
        Assert.Equal(1, delivery.GetProperty("attemptCount").GetInt32());
        Assert.True(delivery.GetProperty("lastAttemptAtUtc").GetDateTimeOffset() <= DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task List_does_not_report_terminal_deliveries_as_unfinished()
    {
        await using WatcherDbContext db = CreateDbContext();
        CommandDeliveryState[] terminalStates =
        [
            CommandDeliveryState.Completed,
            CommandDeliveryState.Stopped,
            CommandDeliveryState.Rejected,
            CommandDeliveryState.Failed,
            CommandDeliveryState.Expired
        ];
        (Guid commandId, Guid userId) = await SeedAsync(db, terminalStates);
        CommandDeliveriesController controller = CreateController(db, userId);

        IActionResult result = await controller.List(commandId, cancellationToken: CancellationToken.None);

        using JsonDocument response = ReadResponse(result);
        Assert.False(response.RootElement.GetProperty("hasUnfinishedDeliveries").GetBoolean());
        var states = response.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("state").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(terminalStates.Select(state => state.ToString()).Order(StringComparer.Ordinal), states);
    }

    private static WatcherDbContext CreateDbContext()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new WatcherDbContext(options);
    }

    private static async Task<(Guid CommandId, Guid UserId)> SeedAsync(
        WatcherDbContext db,
        IReadOnlyList<CommandDeliveryState> states)
    {
        var commandId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = $"test-{userId:N}",
            Email = $"{userId:N}@example.test"
        });
        db.AlertCommands.Add(new AlertCommand
        {
            Id = commandId,
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            InitiatedByUserId = userId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5)
        });

        foreach (CommandDeliveryState state in states)
        {
            var deviceId = Guid.NewGuid();
            var locationId = Guid.NewGuid();
            db.Locations.Add(new Location
            {
                Id = locationId,
                Code = $"loc-{locationId:N}",
                Name = $"Location {state}"
            });
            db.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = locationId,
                Name = $"Device {state}",
                CertificateThumbprint = Convert.ToHexString(Guid.NewGuid().ToByteArray())
            });
            db.CommandDeliveries.Add(new CommandDelivery
            {
                Id = Guid.NewGuid(),
                CommandId = commandId,
                DeviceId = deviceId,
                State = state,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                LastAttemptAtUtc = DateTimeOffset.UtcNow,
                NextAttemptAtUtc = DateTimeOffset.UtcNow.AddSeconds(10),
                AttemptCount = 1
            });
        }

        await db.SaveChangesAsync();
        return (commandId, userId);
    }

    private static CommandDeliveriesController CreateController(WatcherDbContext db, Guid userId)
    {
        ClaimsIdentity identity = new(
            [new Claim(ClaimTypes.Role, "admin"), new Claim("kgta_user_id", userId.ToString())],
            authenticationType: "UnitTest");
        CommandDeliveriesController controller = new(db);
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
        };
        return controller;
    }

    private static JsonDocument ReadResponse(IActionResult result)
    {
        OkObjectResult response = Assert.IsType<OkObjectResult>(result);
        return JsonSerializer.SerializeToDocument(
            response.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
}
