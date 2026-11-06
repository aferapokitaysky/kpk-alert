using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Commands;
using Watcher.Api.Controllers;
using Watcher.Api.Security;
using Watcher.Contracts.DeviceCommands;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class DeviceCommandReceiptsControllerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    public async Task Receive_forbids_missing_or_malformed_device_claim_before_processing_receipt(
        string? deviceIdClaim)
    {
        await using WatcherDbContext db = new(
            new DbContextOptionsBuilder<WatcherDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);
        CommandReceiptProcessor processor = new(db, TimeProvider.System);
        Claim[] claims = deviceIdClaim is null
            ? []
            : [new Claim(DeviceCertificateAuthentication.DeviceIdClaim, deviceIdClaim)];
        DeviceCommandReceiptsController controller = new(processor)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
        CommandReceipt receipt = new(
            DeviceCommandValidator.CurrentSchemaVersion,
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            DeviceCommandState.Accepted,
            DateTimeOffset.UtcNow,
            null);

        IActionResult result = await controller.Receive(receipt, CancellationToken.None);

        Assert.IsType<ForbidResult>(result);
        Assert.Empty(db.DeviceCommandReceipts);
        Assert.Empty(db.AuditEvents);
    }
}
