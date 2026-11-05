using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class AuthEndpointTests
{
    [Fact]
    public async Task Session_returns_only_allowlisted_roles_for_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/session");
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-Test-Role", ["notificator", "auditor", "sound_engineer", "admin", "internal_role"]);

        using HttpResponseMessage response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(body.RootElement.GetProperty("authenticated").GetBoolean());
        Assert.Equal(new[] { "admin", "auditor", "notificator", "sound_engineer" },
            body.RootElement.GetProperty("roles").EnumerateArray().Select(role => role.GetString()));
        Assert.Equal(new[] { "authenticated", "roles" },
            body.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Session_requires_authentication_and_a_valid_local_user_id()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);
        using HttpResponseMessage anonymous = await client.GetAsync("/api/v1/auth/session");
        using HttpRequestMessage invalidIdentity = new(HttpMethod.Get, "/api/v1/auth/session");
        invalidIdentity.Headers.Add("X-Test-User-Id", "not-a-user-id");
        invalidIdentity.Headers.Add("X-Test-Role", "admin");

        using HttpResponseMessage invalidIdentityResponse = await client.SendAsync(invalidIdentity);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, invalidIdentityResponse.StatusCode);
    }

    [Fact]
    public async Task Logout_requires_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateSecureClient(factory);
        var csrfToken = await GetCsrfTokenAsync(client);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_expires_the_session_cookie_for_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);
        var userId = Guid.NewGuid();
        var csrfToken = await GetCsrfTokenAsync(client, userId);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/auth/logout");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);

        using HttpResponseMessage response = await client.SendAsync(request);
        Assert.True(response.StatusCode == HttpStatusCode.NoContent,
            $"Очікувався 204, отримано {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var cookies = response.Headers.GetValues("Set-Cookie").ToArray();
        var sessionCookie = Assert.Single(cookies, value =>
            value.StartsWith("__Host-KgtaAlert.Session=", StringComparison.Ordinal));

        Assert.Contains("expires=", sessionCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", sessionCookie, StringComparison.OrdinalIgnoreCase);
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        AuditEvent audit = await db.AuditEvents.SingleAsync();
        Assert.Equal("auth.logout", audit.EventType);
        Assert.Equal(userId, audit.ActorId);
        Assert.Contains("session", audit.Payload);
        Assert.DoesNotContain("token", audit.Payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Csrf_token_is_returned_with_a_secure_host_only_cookie()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/csrf-token");
        CsrfTokenResponse? token = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("__Host-KgtaAlert.AntiForgery=", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(token?.Token));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Alert_command_rejects_an_authenticated_request_without_a_csrf_token()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: false, useInMemoryDatabase: true);
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/alert-commands")
        {
            Content = JsonContent.Create(new
            {
                action = "start",
                presetCode = "evacuation",
                locationIds = new[] { Guid.NewGuid() }
            })
        };
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest,
            $"Очікувався 400, отримано {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Login_starts_google_oauth_when_credentials_are_configured()
    {
        using WebApplicationFactory<Program> factory = CreateFactory(googleEnabled: true);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using HttpResponseMessage response = await client.GetAsync("/api/v1/auth/login?returnUrl=%2Fdashboard");

        Assert.True(response.StatusCode == HttpStatusCode.Redirect,
            await response.Content.ReadAsStringAsync());
        Assert.NotNull(response.Headers.Location);
        Assert.Equal("accounts.google.com", response.Headers.Location!.Host);
        Assert.Contains("test-client-id", response.Headers.Location.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_session_loses_global_role_after_database_revocation()
    {
        using var factory = new SessionValidationFactory();
        using HttpClient client = CreateSecureClient(factory);
        (Guid userId, Guid roleId) = await SeedAdministratorAsync(factory);
        client.DefaultRequestHeaders.Add("Cookie", CreateSessionCookie(factory, userId, "admin"));

        using HttpResponseMessage beforeRevocation = await client.GetAsync("/api/v1/locations");
        Assert.Equal(HttpStatusCode.OK, beforeRevocation.StatusCode);
        Assert.Single(await beforeRevocation.Content.ReadFromJsonAsync<LocationResponse[]>() ?? []);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            UserRole assignment = await db.UserRoles.SingleAsync(item => item.RoleId == roleId);
            db.UserRoles.Remove(assignment);
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage afterRevocation = await client.GetAsync("/api/v1/locations");
        Assert.Equal(HttpStatusCode.OK, afterRevocation.StatusCode);
        Assert.Empty(await afterRevocation.Content.ReadFromJsonAsync<LocationResponse[]>() ?? []);
    }

    [Fact]
    public async Task Existing_session_is_rejected_after_account_is_blocked()
    {
        using var factory = new SessionValidationFactory();
        using HttpClient client = CreateSecureClient(factory);
        (Guid userId, _) = await SeedAdministratorAsync(factory);
        client.DefaultRequestHeaders.Add("Cookie", CreateSessionCookie(factory, userId, "admin"));

        using HttpResponseMessage beforeBlock = await client.GetAsync("/api/v1/locations");
        Assert.Equal(HttpStatusCode.OK, beforeBlock.StatusCode);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            UserAccount account = await db.Users.SingleAsync(user => user.Id == userId);
            account.IsActive = false;
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage afterBlock = await client.GetAsync("/api/v1/locations");
        Assert.Equal(HttpStatusCode.Unauthorized, afterBlock.StatusCode);
        Assert.Contains(afterBlock.Headers.GetValues("Set-Cookie"), value =>
            value.StartsWith("__Host-KgtaAlert.Session=", StringComparison.Ordinal) &&
            value.Contains("expires=", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<(Guid UserId, Guid RoleId)> SeedAdministratorAsync(
        WebApplicationFactory<Program> factory)
    {
        var userId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = "session-test-user",
            Email = "session@example.test",
            IsActive = true
        });
        db.Roles.Add(new Role { Id = roleId, Code = "admin", Name = "Адміністратор" });
        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId });
        db.Locations.Add(new Location
        {
            Id = Guid.NewGuid(),
            Code = "main",
            Name = "Головний корпус",
            IsActive = true
        });
        await db.SaveChangesAsync();
        return (userId, roleId);
    }

    private static string CreateSessionCookie(WebApplicationFactory<Program> factory, Guid userId, string role)
    {
        CookieAuthenticationOptions options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(
        [
            new Claim("kgta_user_id", userId.ToString()),
            new Claim(ClaimTypes.Role, role)
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        var ticket = new AuthenticationTicket(
            new ClaimsPrincipal(identity), CookieAuthenticationDefaults.AuthenticationScheme);
        return $"__Host-KgtaAlert.Session={options.TicketDataFormat.Protect(ticket)}";
    }

    private sealed record LocationResponse(Guid Id, string Name, string Code);

    [Fact]
    public void Startup_fails_when_google_login_is_enabled_without_credentials()
    {
        using WebApplicationFactory<Program> factory = new AuthTestWebApplicationFactory(
            googleEnabled: true, googleCredentialsConfigured: false);

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public void Production_startup_requires_persistent_data_protection_keys_and_certificate()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DataProtection:KeysPath"] = "",
                    ["DataProtection:CertificatePath"] = "",
                    ["DataProtection:CertificatePassword"] = ""
                }));
        });

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

        Assert.Contains("постійне сховище ключів Data Protection", exception.Message, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool googleEnabled = false) =>
        new AuthTestWebApplicationFactory(googleEnabled, googleCredentialsConfigured: true);

    private static HttpClient CreateSecureClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task<string> GetCsrfTokenAsync(HttpClient client, Guid? userId = null)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        if (userId is Guid id)
        {
            request.Headers.Add("X-Test-User-Id", id.ToString());
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        CsrfTokenResponse? result = await response.Content.ReadFromJsonAsync<CsrfTokenResponse>();
        return result?.Token ?? throw new InvalidOperationException("API не повернув antiforgery token.");
    }

    private sealed record CsrfTokenResponse(string Token);
}

internal sealed class SessionValidationFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<WatcherDbContext>();
            services.RemoveAll<DbContextOptions<WatcherDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<WatcherDbContext>>();
            services.AddDbContext<WatcherDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }
}

internal sealed class AuthTestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly bool _googleEnabled;
    private readonly bool _googleCredentialsConfigured;
    private readonly bool _useInMemoryDatabase;
    private readonly string _inMemoryDatabaseName = Guid.NewGuid().ToString();

    public AuthTestWebApplicationFactory(
        bool googleEnabled,
        bool googleCredentialsConfigured = true,
        bool useInMemoryDatabase = false)
    {
        _googleEnabled = googleEnabled;
        _googleCredentialsConfigured = googleCredentialsConfigured;
        _useInMemoryDatabase = useInMemoryDatabase;
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_googleEnabled)
        {
            builder.ConfigureHostConfiguration(configuration =>
            {
                Dictionary<string, string?> settings = new()
                {
                    ["Authentication:Google:Enabled"] = "true"
                };
                if (_googleCredentialsConfigured)
                {
                    settings["Authentication:Google:ClientId"] = "test-client-id";
                    settings["Authentication:Google:ClientSecret"] = "test-client-secret";
                }

                configuration.AddInMemoryCollection(settings);
            });
        }

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                TestAuthenticationHandler.SchemeName, _ => { });

            if (_googleEnabled || _useInMemoryDatabase)
            {
                services.RemoveAll<WatcherDbContext>();
                services.RemoveAll<DbContextOptions<WatcherDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<WatcherDbContext>>();
                services.AddDbContext<WatcherDbContext>(options =>
                    options.UseInMemoryDatabase(_inMemoryDatabaseName));
            }
        });
    }
}
