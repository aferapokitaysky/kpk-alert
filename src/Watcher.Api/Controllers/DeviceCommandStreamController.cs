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
    DeviceSessionPresenceService presence,
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

        Guid sessionId = await presence.OpenSessionAsync(deviceId, cancellationToken);
        WebSocket? socket = null;
        DeviceCommandWebSocketSession? commandSession = null;
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? heartbeatTask = null;
        try
        {
            socket = await HttpContext.WebSockets.AcceptWebSocketAsync();
            commandSession = await sessions.RegisterAsync(deviceId, sessionId, socket, cancellationToken);
            heartbeatTask = presence.RunHeartbeatAsync(sessionId, () =>
            {
                logger.LogInformation(
                    "Сеанс пристрою {DeviceId} замінено новим підключенням.", deviceId);
                socket.Abort();
            }, heartbeatCancellation.Token);
            var receiveBuffer = new byte[1024];
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(
                    receiveBuffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                await commandSession.CloseAsync(
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
            heartbeatCancellation.Cancel();
            if (heartbeatTask is not null)
            {
                await heartbeatTask;
            }

            if (commandSession is not null)
            {
                sessions.Unregister(commandSession);
            }

            if (socket is { State: WebSocketState.Open })
            {
                socket.Abort();
            }

            try
            {
                await presence.DisconnectAsync(sessionId, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception,
                    "Не вдалося записати завершення сеансу пристрою {DeviceId}.", deviceId);
            }
        }

        return new EmptyResult();
    }
}
