using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class CommandDeliveriesEndpointTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public CommandDeliveriesEndpointTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Delivery_list_uses_command_visibility_rules_for_regular_and_system_commands()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var now = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        Guid userCommandId = await SeedCommandAsync(seed.OperatorId, seed.FirstLocationId, now, 1);
        Guid systemCommandId = await SeedCommandAsync(null, seed.FirstLocationId, now, 0);
        HttpClient client = _fixture.Client;

        using HttpResponseMessage owner = await SendGetAsync(client, userCommandId, seed.OperatorId);
        using HttpResponseMessage unrelated = await SendGetAsync(client, userCommandId, seed.OtherUserId);
        using HttpResponseMessage auditor = await SendGetAsync(client, userCommandId, seed.OtherUserId, "auditor");
        using HttpResponseMessage administrator = await SendGetAsync(client, userCommandId, seed.OtherUserId, "admin");
        using HttpResponseMessage ordinarySystem = await SendGetAsync(client, systemCommandId, seed.OperatorId);
        using HttpResponseMessage auditorSystem = await SendGetAsync(client, systemCommandId, seed.OtherUserId, "auditor");
        using HttpResponseMessage administratorSystem = await SendGetAsync(client, systemCommandId, seed.OtherUserId, "admin");
        using HttpResponseMessage missing = await SendGetAsync(client, Guid.NewGuid(), seed.OtherUserId);
        using HttpResponseMessage anonymous = await client.GetAsync(
            $"/api/v1/alert-commands/{userCommandId}/deliveries");

        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unrelated.StatusCode);
        Assert.Equal("no-store", unrelated.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.OK, auditor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, administrator.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ordinarySystem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, auditorSystem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, administratorSystem.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("no-store", missing.Headers.CacheControl?.ToString());
        await AssertSameNotFoundDetailsAsync(missing, unrelated);
        await AssertSameNotFoundDetailsAsync(missing, ordinarySystem);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [PostgresFact]
    public async Task Delivery_list_returns_a_stable_page_without_device_certificate_data_and_disables_caching()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var createdAtUtc = DateTimeOffset.Parse("2026-11-05T10:00:00Z");
        Guid commandId = await SeedCommandAsync(seed.OperatorId, seed.FirstLocationId, createdAtUtc, 5);
        using HttpResponseMessage response = await SendGetAsync(
            _fixture.Client, commandId, seed.OperatorId, query: "?page=2&pageSize=2");
        var responseBody = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;
        JsonElement[] items = root.GetProperty("items").EnumerateArray().ToArray();
        JsonElement item = items[0];

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal(2, root.GetProperty("page").GetInt32());
        Assert.Equal(2, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, root.GetProperty("totalCount").GetInt32());
        Assert.True(root.GetProperty("hasUnfinishedDeliveries").GetBoolean());
        Assert.Equal(2, items.Length);
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000003"), item.GetProperty("deviceId").GetGuid());
        Assert.Equal("device-3", item.GetProperty("deviceName").GetString());
        Assert.Equal(Guid.Parse("00000000-0000-0000-0000-000000000004"),
            items[1].GetProperty("deviceId").GetGuid());
        Assert.Equal("device-4", items[1].GetProperty("deviceName").GetString());
        Assert.Equal("Pending", item.GetProperty("state").GetString());
        Assert.Equal(createdAtUtc, item.GetProperty("createdAtUtc").GetDateTimeOffset());
        Assert.Equal(createdAtUtc.AddMinutes(5), item.GetProperty("expiresAtUtc").GetDateTimeOffset());
        Assert.Equal(createdAtUtc.AddSeconds(3), item.GetProperty("lastAttemptAtUtc").GetDateTimeOffset());
        Assert.Equal(3, item.GetProperty("attemptCount").GetInt32());
        Assert.False(item.TryGetProperty("certificateThumbprint", out _));
        Assert.False(item.TryGetProperty("secret", out _));
        Assert.DoesNotContain("test-certificate-secret-3", responseBody, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task Delivery_list_returns_an_empty_page_for_a_command_without_deliveries()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid commandId = await SeedCommandAsync(
            seed.OperatorId, seed.FirstLocationId, DateTimeOffset.UtcNow, deliveryCount: 0);
        using HttpResponseMessage response = await SendGetAsync(_fixture.Client, commandId, seed.OperatorId);
        using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Empty(root.GetProperty("items").EnumerateArray());
        Assert.Equal(1, root.GetProperty("page").GetInt32());
        Assert.Equal(50, root.GetProperty("pageSize").GetInt32());
        Assert.Equal(0, root.GetProperty("totalCount").GetInt32());
        Assert.False(root.GetProperty("hasUnfinishedDeliveries").GetBoolean());
    }

    [PostgresFact]
    public async Task Delivery_list_reports_unfinished_deliveries_across_all_pages()
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid commandId = await SeedCommandAsync(
            seed.OperatorId,
            seed.FirstLocationId,
            DateTimeOffset.UtcNow,
            deliveryCount: 5,
            state: CommandDeliveryState.Completed);
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            var activeDeviceId = Guid.Parse("00000000-0000-0000-0000-000000000003");
            CommandDelivery delivery = await db.CommandDeliveries.SingleAsync(
                item => item.CommandId == commandId && item.DeviceId == activeDeviceId);
            delivery.State = CommandDeliveryState.Playing;
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage response = await SendGetAsync(
            _fixture.Client, commandId, seed.OperatorId, query: "?page=1&pageSize=2");
        using JsonDocument document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        JsonElement root = document.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(root.GetProperty("items").EnumerateArray(), item =>
            Assert.Equal("Completed", item.GetProperty("state").GetString()));
        Assert.True(root.GetProperty("hasUnfinishedDeliveries").GetBoolean());

        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            List<CommandDelivery> deliveries = await db.CommandDeliveries
                .Where(item => item.CommandId == commandId)
                .ToListAsync();
            foreach (CommandDelivery delivery in deliveries)
            {
                delivery.State = CommandDeliveryState.Completed;
            }

            await db.SaveChangesAsync();
        }

        using HttpResponseMessage completedResponse = await SendGetAsync(
            _fixture.Client, commandId, seed.OperatorId, query: "?page=1&pageSize=2");
        using JsonDocument completedDocument = await JsonDocument.ParseAsync(
            await completedResponse.Content.ReadAsStreamAsync());
        Assert.False(completedDocument.RootElement.GetProperty("hasUnfinishedDeliveries").GetBoolean());
    }

    [PostgresTheory]
    [InlineData("?page=0")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    [InlineData("?page=2147483647&pageSize=100")]
    public async Task Delivery_list_rejects_invalid_pagination(string query)
    {
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid commandId = await SeedCommandAsync(
            seed.OperatorId, seed.FirstLocationId, DateTimeOffset.UtcNow, deliveryCount: 0);
        using HttpResponseMessage response = await SendGetAsync(
            _fixture.Client, commandId, seed.OperatorId, query: query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private async Task<Guid> SeedCommandAsync(
        Guid? initiatedByUserId,
        Guid locationId,
        DateTimeOffset createdAtUtc,
        int deliveryCount,
        CommandDeliveryState state = CommandDeliveryState.Pending)
    {
        var commandId = Guid.NewGuid();
        using WatcherDbContext db = _fixture.CreateDbContext();
        db.AlertCommands.Add(new AlertCommand
        {
            Id = commandId,
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            InitiatedByUserId = initiatedByUserId,
            CreatedAtUtc = createdAtUtc,
            ExpiresAtUtc = createdAtUtc.AddMinutes(5)
        });

        for (var index = 1; index <= deliveryCount; index++)
        {
            var deviceId = Guid.Parse($"00000000-0000-0000-0000-{index:D12}");
            db.Devices.Add(new Device
            {
                Id = deviceId,
                LocationId = locationId,
                Name = $"device-{index}",
                CertificateThumbprint = $"test-certificate-secret-{index}"
            });
            db.CommandDeliveries.Add(new CommandDelivery
            {
                Id = Guid.NewGuid(),
                CommandId = commandId,
                DeviceId = deviceId,
                State = state,
                CreatedAtUtc = createdAtUtc,
                ExpiresAtUtc = createdAtUtc.AddMinutes(5),
                LastAttemptAtUtc = createdAtUtc.AddSeconds(index),
                AttemptCount = index
            });
        }

        await db.SaveChangesAsync();
        return commandId;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        Guid commandId,
        Guid userId,
        string? role = null,
        string query = "")
    {
        using HttpRequestMessage request = new(HttpMethod.Get,
            $"/api/v1/alert-commands/{commandId}/deliveries{query}");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }

        return await client.SendAsync(request);
    }

    private static async Task AssertSameNotFoundDetailsAsync(
        HttpResponseMessage expected,
        HttpResponseMessage actual)
    {
        using var expectedBody = JsonDocument.Parse(await expected.Content.ReadAsStringAsync());
        using var actualBody = JsonDocument.Parse(await actual.Content.ReadAsStringAsync());
        foreach (var propertyName in new[] { "type", "title", "status" })
        {
            Assert.Equal(
                expectedBody.RootElement.GetProperty(propertyName).ToString(),
                actualBody.RootElement.GetProperty(propertyName).ToString());
        }
    }
}
