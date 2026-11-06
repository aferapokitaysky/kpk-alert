using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Watcher.Api.Controllers;
using Watcher.Api.Security;

namespace Watcher.UnitTests;

public sealed class DeviceIdentityControllerTests
{
    [Theory]
    [InlineData(null, "33333333-3333-3333-3333-333333333333")]
    [InlineData("22222222-2222-2222-2222-222222222222", "not-a-guid")]
    public void GetIdentity_rejects_an_incomplete_authenticated_device_identity(
        string? deviceId,
        string locationId)
    {
        Claim[] claims =
        [
            new Claim(DeviceCertificateAuthentication.LocationIdClaim, locationId)
        ];
        if (deviceId is not null)
        {
            claims = [.. claims, new Claim(DeviceCertificateAuthentication.DeviceIdClaim, deviceId)];
        }

        var controller = new DeviceIdentityController
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };

        Assert.IsType<UnauthorizedResult>(controller.GetIdentity());
    }
}
