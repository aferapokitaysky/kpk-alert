using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class GoogleTicketTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Creating_google_ticket_rejects_unverified_or_missing_email_verification(bool? emailVerified)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();

        TicketResult result = await RunTicketEventAsync(factory, emailVerified);

        Assert.NotNull(result.Failure);
        Assert.Contains("підтвердив", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_google_ticket_rejects_an_unregistered_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();

        TicketResult result = await RunTicketEventAsync(factory, emailVerified: true);

        Assert.NotNull(result.Failure);
        Assert.Contains("не зареєстрований", result.Failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_google_ticket_rejects_an_inactive_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();

        TicketResult result = await RunTicketEventAsync(factory, emailVerified: true, accountIsActive: false);

        Assert.NotNull(result.Failure);
    }

    [Fact]
    public async Task Creating_google_ticket_adds_local_user_id_and_global_role()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();

        TicketResult result = await RunTicketEventAsync(
            factory, emailVerified: true, accountIsActive: true, addGlobalAdministrator: true);

        Assert.Null(result.Failure);
        Assert.Contains(result.Claims, claim =>
            claim.Type == "kgta_user_id" && claim.Value == result.UserId.ToString());
        Assert.Contains(result.Claims, claim => claim.Type == ClaimTypes.Role && claim.Value == "admin");
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new AuthTestWebApplicationFactory(googleEnabled: true);

    private static async Task<TicketResult> RunTicketEventAsync(
        WebApplicationFactory<Program> factory,
        bool? emailVerified,
        bool? accountIsActive = null,
        bool addGlobalAdministrator = false)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        var userId = Guid.NewGuid();
        const string externalSubject = "google-subject";
        if (accountIsActive.HasValue)
        {
            db.Users.Add(new UserAccount
            {
                Id = userId,
                ExternalSubject = externalSubject,
                Email = "operator@example.test",
                IsActive = accountIsActive.Value
            });
        }

        if (addGlobalAdministrator)
        {
            Role administrator = new() { Id = Guid.NewGuid(), Code = "admin", Name = "Адміністратор" };
            db.Roles.Add(administrator);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = administrator.Id
            });
        }

        await db.SaveChangesAsync();
        GoogleOptions options = factory.Services.GetRequiredService<IOptionsMonitor<GoogleOptions>>().Get("Google");
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, externalSubject)], "Google"));
        Dictionary<string, bool> userFields = new();
        if (emailVerified is bool isVerified)
        {
            userFields["email_verified"] = isVerified;
        }

        using var userJson = JsonDocument.Parse(JsonSerializer.Serialize(userFields));
        using var tokenJson = JsonDocument.Parse("{}");
        using var tokenResponse = OAuthTokenResponse.Success(tokenJson);
        using HttpClient backchannel = new();
        AuthenticationScheme scheme = new("Google", "Google", typeof(GoogleHandler));
        OAuthCreatingTicketContext context = new(
            principal,
            new AuthenticationProperties(),
            new DefaultHttpContext { RequestServices = scope.ServiceProvider },
            scheme,
            options,
            backchannel,
            tokenResponse,
            userJson.RootElement);

        await options.Events.OnCreatingTicket(context);

        return new TicketResult(context.Result?.Failure, context.Identity?.Claims.ToArray() ?? [], userId);
    }

    private sealed record TicketResult(Exception? Failure, Claim[] Claims, Guid UserId);
}
