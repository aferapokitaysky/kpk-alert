using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Watcher.Api.Commands;
using Watcher.Api.Security;
using Watcher.Contracts.DeviceCommands;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = DeviceCertificateAuthentication.Scheme)]
[Route("api/v1/device/command-receipts")]
public sealed class DeviceCommandReceiptsController(CommandReceiptProcessor processor) : ControllerBase
{
    private const int MaximumRequestBodySizeBytes = 8 * 1024;

    [HttpPost]
    [Consumes("application/json")]
    [RequestSizeLimit(MaximumRequestBodySizeBytes)]
    [RequestBodySizeLimit(MaximumRequestBodySizeBytes)]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Receive(
        [FromBody] CommandReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(
                User.FindFirst(DeviceCertificateAuthentication.DeviceIdClaim)?.Value,
                out Guid deviceId))
        {
            return Forbid();
        }

        CommandReceiptOutcome result = await processor.ProcessAsync(deviceId, receipt, cancellationToken);
        return result switch
        {
            CommandReceiptOutcome.Recorded => Accepted(),
            CommandReceiptOutcome.Duplicate => Ok(new { duplicate = true }),
            CommandReceiptOutcome.NotFound => NotFound(),
            CommandReceiptOutcome.Conflict => Conflict(),
            CommandReceiptOutcome.Invalid => Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Перевірте версію, ідентифікатори, послідовність та код причини квитанції."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), result, "Невідомий результат квитанції.")
        };
    }
}
