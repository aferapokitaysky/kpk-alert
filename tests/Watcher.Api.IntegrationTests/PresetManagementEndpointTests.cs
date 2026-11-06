using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Api.Contracts;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class PresetManagementEndpointTests
{
    [Fact]
    public async Task Global_administrator_can_manage_and_publish_a_preset_with_audit()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var administratorId = Guid.NewGuid();
        await SeedRoleAsync(factory, administratorId, "admin", locationId: null);
        using HttpClient client = CreateSecureClient(factory);

        using HttpResponseMessage created = await SendAsync(
            client,
            administratorId,
            HttpMethod.Post,
            "/api/v1/presets",
            new CreateSoundPresetRequest("evacuation", "  Евакуація  ", 1, new string('a', 64)));
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"Очікувався 201, отримано {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        AssertNoStore(created);
        SoundPresetResponse? createdPreset = await created.Content.ReadFromJsonAsync<SoundPresetResponse>();

        Assert.Equal("/api/v1/presets/evacuation", created.Headers.Location?.AbsolutePath);
        Assert.Equal("Евакуація", createdPreset?.Name);
        Assert.False(createdPreset?.IsPublished);

        using HttpResponseMessage publicList = await SendAsync(
            client, administratorId, HttpMethod.Get, "/api/v1/presets");
        Assert.Equal(HttpStatusCode.OK, publicList.StatusCode);
        Assert.Empty(await publicList.Content.ReadFromJsonAsync<SoundPresetResponse[]>() ?? []);

        using HttpResponseMessage managedList = await SendAsync(
            client, administratorId, HttpMethod.Get, "/api/v1/presets/manage");
        SoundPresetResponse[] managedPresets = await managedList.Content.ReadFromJsonAsync<SoundPresetResponse[]>() ?? [];
        Assert.Equal(HttpStatusCode.OK, managedList.StatusCode);
        AssertNoStore(managedList);
        Assert.Equal("evacuation", Assert.Single(managedPresets).Code);

        using HttpResponseMessage managedDetails = await SendAsync(
            client, administratorId, HttpMethod.Get, "/api/v1/presets/evacuation");
        Assert.Equal(HttpStatusCode.OK, managedDetails.StatusCode);
        AssertNoStore(managedDetails);
        Assert.False((await managedDetails.Content.ReadFromJsonAsync<SoundPresetResponse>())?.IsPublished);

        using HttpResponseMessage updated = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/evacuation",
            new UpdateSoundPresetRequest("Евакуація з корпусу", 2, new string('b', 64)));
        SoundPresetResponse? updatedPreset = await updated.Content.ReadFromJsonAsync<SoundPresetResponse>();
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        AssertNoStore(updated);
        Assert.Equal("Евакуація з корпусу", updatedPreset?.Name);
        Assert.Equal(new string('b', 64), updatedPreset?.ManifestHash);
        Assert.False(updatedPreset?.IsPublished);

        using HttpResponseMessage published = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/evacuation/publication",
            new SetSoundPresetPublicationRequest(true));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        AssertNoStore(published);
        Assert.True((await published.Content.ReadFromJsonAsync<SoundPresetResponse>())?.IsPublished);

        using HttpResponseMessage publishedUpdate = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/evacuation",
            new UpdateSoundPresetRequest("Не має застосуватися", 3, new string('c', 64)));
        Assert.Equal(HttpStatusCode.Conflict, publishedUpdate.StatusCode);

        using HttpResponseMessage repeatedPublish = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/evacuation/publication",
            new SetSoundPresetPublicationRequest(true));
        Assert.Equal(HttpStatusCode.OK, repeatedPublish.StatusCode);

        using HttpResponseMessage publishedList = await SendAsync(
            client, administratorId, HttpMethod.Get, "/api/v1/presets");
        SoundPresetResponse[] publishedPresets = await publishedList.Content.ReadFromJsonAsync<SoundPresetResponse[]>() ?? [];
        Assert.Equal("evacuation", Assert.Single(publishedPresets).Code);

        using HttpResponseMessage unpublished = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/evacuation/publication",
            new SetSoundPresetPublicationRequest(false));
        Assert.Equal(HttpStatusCode.OK, unpublished.StatusCode);
        AssertNoStore(unpublished);
        Assert.False((await unpublished.Content.ReadFromJsonAsync<SoundPresetResponse>())?.IsPublished);

        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        AuditEvent[] events = await db.AuditEvents.OrderBy(item => item.EventType).ToArrayAsync();
        Assert.Equal(4, events.Length);
        Assert.All(events, auditEvent => Assert.Equal(administratorId, auditEvent.ActorId));
        Assert.Equal(1, events.Count(item => item.EventType == "sound_preset.created"));
        Assert.Equal(1, events.Count(item => item.EventType == "sound_preset.updated"));
        Assert.Equal(2, events.Count(item => item.EventType == "sound_preset.publication_changed"));
        Assert.All(events, auditEvent => Assert.Equal("user", auditEvent.ActorType));
    }

    [Fact]
    public async Task Publication_request_requires_is_published_and_does_not_change_state_or_audit()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var administratorId = Guid.NewGuid();
        await SeedRoleAsync(factory, administratorId, "admin", locationId: null);
        using IServiceScope seedScope = factory.Services.CreateScope();
        WatcherDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        SoundPreset preset = CreatePreset("evacuation");
        seedDb.SoundPresets.Add(preset);
        await seedDb.SaveChangesAsync();

        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = new(HttpMethod.Put, "/api/v1/presets/evacuation/publication")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Test-User-Id", administratorId.ToString());
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, administratorId, role: null));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoStore(response);
        await seedDb.Entry(preset).ReloadAsync();
        Assert.False(preset.IsPublished);
        Assert.Empty(await seedDb.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task Concurrent_publication_prevents_stale_draft_metadata_update()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            WatcherDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            seedDb.SoundPresets.Add(CreatePreset("evacuation"));
            await seedDb.SaveChangesAsync();
        }

        using IServiceScope updateScope = factory.Services.CreateScope();
        using IServiceScope publicationScope = factory.Services.CreateScope();
        WatcherDbContext updateDb = updateScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        WatcherDbContext publicationDb = publicationScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        SoundPreset staleDraft = await updateDb.SoundPresets.SingleAsync(item => item.Code == "evacuation");
        SoundPreset publishingDraft = await publicationDb.SoundPresets.SingleAsync(item => item.Code == "evacuation");

        publishingDraft.IsPublished = true;
        await publicationDb.SaveChangesAsync();
        staleDraft.Name = "Змінена після публікації";

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => updateDb.SaveChangesAsync());

        using IServiceScope verifyScope = factory.Services.CreateScope();
        WatcherDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        SoundPreset persisted = await verifyDb.SoundPresets.SingleAsync(item => item.Code == "evacuation");
        Assert.True(persisted.IsPublished);
        Assert.Equal("evacuation", persisted.Name);
    }

    [Fact]
    public async Task Location_scoped_administrator_cannot_manage_global_presets()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        await SeedRoleAsync(factory, userId, "admin", Guid.NewGuid());
        using HttpClient client = CreateSecureClient(factory);

        using HttpResponseMessage managedList = await SendAsync(
            client, userId, HttpMethod.Get, "/api/v1/presets/manage", role: "admin");
        using HttpResponseMessage create = await SendAsync(
            client,
            userId,
            HttpMethod.Post,
            "/api/v1/presets",
            new CreateSoundPresetRequest("test", "Test", 1, new string('a', 64)),
            role: "admin");

        Assert.True(managedList.StatusCode == HttpStatusCode.Forbidden,
            $"Очікувався 403, отримано {(int)managedList.StatusCode}: {await managedList.Content.ReadAsStringAsync()}");
        Assert.True(create.StatusCode == HttpStatusCode.Forbidden,
            $"Очікувався 403, отримано {(int)create.StatusCode}: {await create.Content.ReadAsStringAsync()}");
    }

    [Fact]
    public async Task Inactive_global_administrator_cannot_manage_presets_with_a_stale_admin_claim()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var administratorId = Guid.NewGuid();
        await SeedRoleAsync(factory, administratorId, "admin", locationId: null);
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            UserAccount account = await db.Users.SingleAsync(user => user.Id == administratorId);
            account.IsActive = false;
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateSecureClient(factory);
        using HttpResponseMessage managedList = await SendAsync(
            client, administratorId, HttpMethod.Get, "/api/v1/presets/manage", role: "admin");
        using HttpResponseMessage create = await SendAsync(
            client,
            administratorId,
            HttpMethod.Post,
            "/api/v1/presets",
            new CreateSoundPresetRequest("evacuation", "Евакуація", 1, new string('a', 64)),
            role: "admin");

        Assert.Equal(HttpStatusCode.Forbidden, managedList.StatusCode);
        AssertNoStore(managedList);
        Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        AssertNoStore(create);
        using IServiceScope verifyScope = factory.Services.CreateScope();
        WatcherDbContext verifyDb = verifyScope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        Assert.Empty(await verifyDb.SoundPresets.ToArrayAsync());
        Assert.Empty(await verifyDb.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task Create_requires_a_csrf_token()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var administratorId = Guid.NewGuid();
        await SeedRoleAsync(factory, administratorId, "admin", locationId: null);
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/presets")
        {
            Content = JsonContent.Create(
                new CreateSoundPresetRequest("evacuation", "Евакуація", 1, new string('a', 64)))
        };
        request.Headers.Add("X-Test-User-Id", administratorId.ToString());

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        Assert.Empty(await db.SoundPresets.ToArrayAsync());
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Theory]
    [InlineData("Invalid Code", "Test", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("valid_code", "Test", "not-a-sha256-hash")]
    [InlineData("valid_code", "   ", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Create_rejects_invalid_code_name_or_manifest_hash(
        string code,
        string name,
        string manifestHash)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var administratorId = Guid.NewGuid();
        await SeedRoleAsync(factory, administratorId, "admin", locationId: null);
        using HttpClient client = CreateSecureClient(factory);

        using HttpResponseMessage response = await SendAsync(
            client,
            administratorId,
            HttpMethod.Post,
            "/api/v1/presets",
            new CreateSoundPresetRequest(code, name, 1, manifestHash));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        Assert.Empty(await db.SoundPresets.ToArrayAsync());
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
    }

    [Fact]
    public async Task Create_returns_conflict_for_an_existing_code_and_missing_preset_operations_return_not_found()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var administratorId = Guid.NewGuid();
        await SeedRoleAsync(factory, administratorId, "admin", locationId: null);
        using HttpClient client = CreateSecureClient(factory);
        CreateSoundPresetRequest request = new("evacuation", "Евакуація", 1, new string('a', 64));

        using HttpResponseMessage first = await SendAsync(
            client, administratorId, HttpMethod.Post, "/api/v1/presets", request);
        using HttpResponseMessage duplicate = await SendAsync(
            client, administratorId, HttpMethod.Post, "/api/v1/presets", request);
        using HttpResponseMessage missingUpdate = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/missing",
            new UpdateSoundPresetRequest("Назва", 1, new string('b', 64)));
        using HttpResponseMessage missingDetails = await SendAsync(
            client, administratorId, HttpMethod.Get, "/api/v1/presets/missing");
        using HttpResponseMessage missingPublication = await SendAsync(
            client,
            administratorId,
            HttpMethod.Put,
            "/api/v1/presets/missing/publication",
            new SetSoundPresetPublicationRequest(true));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingDetails.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingPublication.StatusCode);

        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        Assert.Single(await db.SoundPresets.ToArrayAsync());
        Assert.Single(await db.AuditEvents.ToArrayAsync());
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new AuthTestWebApplicationFactory(googleEnabled: false, useInMemoryDatabase: true);

    private static SoundPreset CreatePreset(string code) => new()
    {
        Id = Guid.NewGuid(),
        Code = code,
        Name = code,
        Priority = 1,
        ManifestHash = new string('a', 64),
        IsPublished = false
    };

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateSecureClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static async Task SeedRoleAsync(
        WebApplicationFactory<Program> factory,
        Guid userId,
        string roleCode,
        Guid? locationId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = $"preset-test-{userId}",
            Email = $"{userId}@example.test",
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        Role role = new() { Id = Guid.NewGuid(), Code = roleCode, Name = roleCode };
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id,
            LocationId = locationId
        });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        Guid userId,
        HttpMethod method,
        string path,
        object? body = null,
        string? role = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (method != HttpMethod.Get)
        {
            request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, userId, role));
        }

        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client, Guid userId, string? role)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }

        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("CSRF endpoint did not return a token.");
    }
}
