using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class AuditEventsEndpointTests
{
    [Fact]
    public async Task Audit_events_require_authentication_and_auditor_or_admin_role()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        using HttpClient client = CreateSecureClient(factory);
        using HttpResponseMessage anonymous = await client.GetAsync("/api/v1/audit-events");
        var operatorId = Guid.NewGuid();
        await SeedRoleAsync(factory, operatorId, "notificator");
        using HttpRequestMessage operatorRequest = CreateRequest(operatorId, "notificator");
        using HttpResponseMessage operatorResponse = await client.SendAsync(operatorRequest);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, operatorResponse.StatusCode);
    }

    [Theory]
    [InlineData("auditor")]
    [InlineData("admin")]
    public async Task Audit_events_return_a_stable_newest_first_page(string role)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        await SeedEventsAsync(factory, occurredAt, userId);
        await SeedRoleAsync(factory, userId, role);
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = CreateRequest(userId, role, "?page=1&pageSize=1");

        using HttpResponseMessage response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement root = body.RootElement;
        JsonElement item = Assert.Single(root.GetProperty("items").EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(1, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(2, root.GetProperty("totalCount").GetInt32());
        Assert.Equal("auth.logout", item.GetProperty("eventType").GetString());
        Assert.Equal("user", item.GetProperty("actorType").GetString());
        Assert.Equal(userId, item.GetProperty("actorId").GetGuid());
        Assert.Equal("session", item.GetProperty("payload").GetProperty("method").GetString());
    }

    [Fact]
    public async Task Audit_events_can_be_filtered_by_exact_event_type()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        await SeedEventsAsync(factory, DateTimeOffset.UtcNow, userId);
        await SeedRoleAsync(factory, userId, "auditor");
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = CreateRequest(userId, "auditor", "?eventType=auth.login.succeeded");

        using HttpResponseMessage response = await client.SendAsync(request);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(1, body.RootElement.GetProperty("totalCount").GetInt32());
        JsonElement item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("auth.login.succeeded", item.GetProperty("eventType").GetString());
    }

    [Fact]
    public async Task Audit_events_deny_access_when_the_role_claim_has_been_revoked()
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        await SeedRoleAsync(factory, userId, "auditor");
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
            UserRole assignment = await db.UserRoles.SingleAsync();
            db.UserRoles.Remove(assignment);
            await db.SaveChangesAsync();
        }

        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = CreateRequest(userId, "auditor");
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?eventType=abcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyzabcdefghijklmnopqrstuvwxyz")]
    public async Task Audit_events_reject_invalid_pagination_and_filter_values(string query)
    {
        using WebApplicationFactory<Program> factory = CreateFactory();
        var userId = Guid.NewGuid();
        await SeedRoleAsync(factory, userId, "auditor");
        using HttpClient client = CreateSecureClient(factory);
        using HttpRequestMessage request = CreateRequest(userId, "auditor", query);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() => new AuthTestWebApplicationFactory(
        googleEnabled: false, useInMemoryDatabase: true);

    private static HttpClient CreateSecureClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

    private static HttpRequestMessage CreateRequest(Guid userId, string role, string query = "")
    {
        HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/audit-events{query}");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-Test-Role", role);
        return request;
    }

    private static async Task SeedEventsAsync(
        WebApplicationFactory<Program> factory, DateTimeOffset occurredAt, Guid userId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.AuditEvents.AddRange(
            new AuditEvent
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                EventType = "auth.login.succeeded",
                ActorType = "user",
                ActorId = userId,
                Payload = "{\"method\":\"google\"}",
                OccurredAtUtc = occurredAt
            },
            new AuditEvent
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                EventType = "auth.logout",
                ActorType = "user",
                ActorId = userId,
                Payload = "{\"method\":\"session\"}",
                OccurredAtUtc = occurredAt
            });
        await db.SaveChangesAsync();
    }

    private static async Task SeedRoleAsync(WebApplicationFactory<Program> factory, Guid userId, string roleCode)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = $"test-{userId}",
            Email = $"{userId}@example.test"
        });
        Role role = new() { Id = Guid.NewGuid(), Code = roleCode, Name = roleCode };
        db.Roles.Add(role);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            RoleId = role.Id
        });
        await db.SaveChangesAsync();
    }
}
