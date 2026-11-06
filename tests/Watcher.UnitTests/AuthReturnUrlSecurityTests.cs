using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Watcher.Api.Controllers;

namespace Watcher.UnitTests;

public sealed class AuthReturnUrlSecurityTests
{
    [Theory]
    [InlineData("//attacker.example/steal")]
    [InlineData("/\\attacker.example/steal")]
    [InlineData("\\\\attacker.example/steal")]
    public void Login_rejects_external_authority_redirect_shapes(string returnUrl)
    {
        AuthController controller = new(CreateConfiguration())
        {
            Url = new UrlHelper(new ActionContext(
                new DefaultHttpContext(), new RouteData(), new ActionDescriptor()))
        };

        ChallengeResult result = Assert.IsType<ChallengeResult>(controller.Login(returnUrl));

        Assert.Equal("/", result.Properties?.RedirectUri);
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Google:Enabled"] = "true"
        })
        .Build();
}

