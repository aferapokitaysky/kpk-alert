using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Watcher.Api.Controllers;

namespace Watcher.UnitTests;

public sealed class AuthControllerTests
{
    [Fact]
    public void Login_returns_service_unavailable_when_google_is_disabled()
    {
        AuthController controller = new(CreateConfiguration(false));

        ObjectResult result = Assert.IsType<ObjectResult>(controller.Login());

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example/steal", "/")]
    [InlineData("/dashboard", "/dashboard")]
    public void Login_uses_google_and_only_allows_local_return_urls(string requestedUrl, string expectedUrl)
    {
        AuthController controller = new(CreateConfiguration(true))
        {
            Url = new UrlHelper(new ActionContext(
                new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

        ChallengeResult result = Assert.IsType<ChallengeResult>(controller.Login(requestedUrl));

        Assert.Equal("Google", result.AuthenticationSchemes.Single());
        Assert.Equal(expectedUrl, result.Properties?.RedirectUri);
    }

    private static IConfiguration CreateConfiguration(bool enabled) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Google:Enabled"] = enabled.ToString()
            })
            .Build();
}
