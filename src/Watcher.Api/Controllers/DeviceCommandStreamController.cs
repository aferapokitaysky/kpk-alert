using System.Net.WebSockets;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Watcher.Api.Delivery;
using Watcher.Api.Security;

namespace Watcher.Api.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = DeviceCertificateAuthentication.Scheme)]
[Route("api/v1/device/commands")]
public sealed class DeviceCommandStreamController(
    DeviceCommandSessionRegistry sessions,
    IConfiguration configuration,
    ILogger<DeviceCommandStreamController> logger) : ControllerBase
{
    [HttpGet("stream")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Connect(CancellationToken cancellationToken)
    {
        var devicePort = configuration.GetValue<int?>("DeviceListener:Port");
        if (devicePort is null || HttpContext.Connection.LocalPort != devicePort)
        {
            return NotFound();
        }

        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            return BadRequest();
        }

        if (!Guid.TryParse(
                User.FindFirst(DeviceCertificateAuthentication.DeviceIdClaim)?.Value,
                out Guid deviceId))
        {
            return Forbid();
        }

        using WebSocket socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
        DeviceCommandWebSocketSession session = await sessions.RegisterAsync(
            deviceId, socket, cancellationToken);
        var receiveBuffer = new byte[1024];
        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    receiveBuffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                await session.CloseAsync(
                    WebSocketCloseStatus.PolicyViolation,
                    "Пристрій не може надсилати повідомлення в каналі команд.",
                    cancellationToken);
                break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("З'єднання пристрою {DeviceId} закрито після скасування запиту.", deviceId);
        }
        catch (WebSocketException exception)
        {
            logger.LogInformation(
                "З'єднання пристрою {DeviceId} завершено з помилкою WebSocket ({ErrorType}).",
                deviceId,
                exception.GetType().Name);
        }
        finally
        {
            sessions.Unregister(session);
        }

        return new EmptyResult();
    }
}
