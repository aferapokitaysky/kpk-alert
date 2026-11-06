using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class PresetsEndpointTests
{
    [Fact]
    public async Task List_requires_an_authenticated_user()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/api/v1/presets");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task List_returns_published_presets_to_a_local_operator()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            db.Users.Add(new UserAccount
            {
                Id = userId,
                ExternalSubject = $"presets-test-{userId}",
                Email = $"{userId}@example.test",
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            Role role = new() { Id = Guid.NewGuid(), Code = "notificator", Name = "Оператор" };
            db.Roles.Add(role);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = role.Id,
                LocationId = Guid.NewGuid()
            });
            db.SoundPresets.AddRange(
                new SoundPreset
                {
                    Id = Guid.NewGuid(),
                    Code = "evacuation",
                    Name = "Евакуація",
                    Priority = 1,
                    ManifestHash = new string('a', 64),
                    IsPublished = true
                },
                new SoundPreset
                {
                    Id = Guid.NewGuid(),
                    Code = "draft",
                    Name = "Чернетка",
                    Priority = 0,
                    ManifestHash = new string('b', 64),
                    IsPublished = false
                });
            await db.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/presets");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"Очікувався 200, отримано {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("evacuation", body);
        Assert.DoesNotContain("draft", body);
    }

    [Fact]
    public async Task List_forbids_an_authenticated_user_without_an_operator_role()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/presets");
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task List_forbids_an_inactive_administrator_with_a_stale_admin_claim()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            db.Users.Add(new UserAccount
            {
                Id = userId,
                ExternalSubject = $"inactive-presets-test-{userId}",
                Email = $"inactive-{userId}@example.test",
                IsActive = false,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            Role role = new() { Id = Guid.NewGuid(), Code = "admin", Name = "Адміністратор" };
            db.Roles.Add(role);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                RoleId = role.Id,
                LocationId = null
            });
            db.SoundPresets.Add(new SoundPreset
            {
                Id = Guid.NewGuid(),
                Code = "evacuation",
                Name = "Евакуація",
                Priority = 1,
                ManifestHash = new string('a', 64),
                IsPublished = true
            });
            await db.SaveChangesAsync();
        }

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/presets");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", "admin");

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("evacuation", await response.Content.ReadAsStringAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new AuthTestWebApplicationFactory(googleEnabled: false, useInMemoryDatabase: true);
}
