using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class PostgresAlertCommandApiTests : IClassFixture<PostgresApiFixture>
{
    private readonly PostgresApiFixture _fixture;

    public PostgresAlertCommandApiTests(PostgresApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Create_persists_command_targets_audit_and_outbox_in_postgresql()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var idempotencyKey = Guid.NewGuid();

        using HttpResponseMessage response = await SendCreateAsync(
            client, seed.OperatorId, idempotencyKey, [seed.FirstLocationId]);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        CommandResponse? responseBody = await response.Content.ReadFromJsonAsync<CommandResponse>();
        Assert.NotNull(responseBody);
        using WatcherDbContext db = _fixture.CreateDbContext();
        AlertCommand command = await db.AlertCommands.Include(item => item.Targets).SingleAsync();
        OutboxMessage outbox = await db.OutboxMessages.SingleAsync();
        AuditEvent audit = await db.AuditEvents.SingleAsync();
        Assert.Equal(idempotencyKey, command.IdempotencyKey);
        TimeSpan persistedTtl = command.ExpiresAtUtc - command.CreatedAtUtc;
        Assert.InRange(
            (persistedTtl - TimeSpan.FromMinutes(AlertCommand.DefaultValidityMinutes)).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMicroseconds(1));
        Assert.InRange(
            (responseBody.ExpiresAtUtc - command.ExpiresAtUtc).Duration(),
            TimeSpan.Zero,
            TimeSpan.FromMicroseconds(1));
        Assert.Equal(seed.FirstLocationId, Assert.Single(command.Targets).LocationId);
        Assert.Equal(1, command.Targets.Single().Sequence);
        Assert.Equal(command.Id, outbox.CommandId);
        Assert.Equal("alert_command.created", outbox.EventType);
        Assert.Null(outbox.ProcessedAtUtc);
        Assert.Equal(command.Id, audit.Payload.DeserializeCommandId());
    }

    [PostgresFact]
    public async Task Concurrent_commands_receive_distinct_increasing_sequences_for_the_same_location()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        const int commandCount = 12;
        var csrfToken = await GetCsrfTokenAsync(client, seed.OperatorId);
        ConcurrentBag<HttpResponseMessage> responses = [];
        try
        {
            using (WatcherDbContext setupDb = _fixture.CreateDbContext())
            {
                await setupDb.Database.ExecuteSqlRawAsync("""
                    DROP TRIGGER IF EXISTS wait_for_location_command_sequence_contenders ON location_command_sequences;
                    DROP FUNCTION IF EXISTS wait_for_location_command_sequence_contenders();
                    DROP SEQUENCE IF EXISTS location_command_sequence_test_arrivals;
                    """);

                var barrierSql = $"""
                CREATE SEQUENCE location_command_sequence_test_arrivals START WITH 1;

                CREATE FUNCTION wait_for_location_command_sequence_contenders() RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    arrival_count bigint;
                    deadline timestamptz := clock_timestamp() + interval '15 seconds';
                BEGIN
                    PERFORM nextval('location_command_sequence_test_arrivals');
                    LOOP
                        SELECT last_value INTO arrival_count
                        FROM location_command_sequence_test_arrivals;

                        EXIT WHEN arrival_count >= {commandCount};
                        IF clock_timestamp() >= deadline THEN
                            RAISE EXCEPTION 'Timed out waiting for sequence allocation contenders';
                        END IF;

                        PERFORM pg_sleep(0.01);
                    END LOOP;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER wait_for_location_command_sequence_contenders
                BEFORE INSERT ON location_command_sequences
                FOR EACH ROW
                EXECUTE FUNCTION wait_for_location_command_sequence_contenders();
                """;
                await setupDb.Database.ExecuteSqlRawAsync(barrierSql);
            }

            TaskCompletionSource<bool> startGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<HttpResponseMessage>[] createTasks = Enumerable.Range(0, commandCount)
                .Select(async _ =>
                {
                    await startGate.Task;
                    HttpResponseMessage response = await SendCreateAsync(
                        client, seed.OperatorId, Guid.NewGuid(), [seed.FirstLocationId], csrfToken);
                    responses.Add(response);
                    return response;
                })
                .ToArray();
            startGate.SetResult(true);
            HttpResponseMessage[] completedResponses = await Task.WhenAll(createTasks);

            Assert.All(completedResponses, response => Assert.Equal(HttpStatusCode.Accepted, response.StatusCode));
            using WatcherDbContext db = _fixture.CreateDbContext();
            var contenderCount = await db.Database.SqlQueryRaw<long>("""
                SELECT last_value AS "Value"
                FROM location_command_sequence_test_arrivals
                """).SingleAsync();
            var sequences = await db.CommandTargets
                .Where(target => target.LocationId == seed.FirstLocationId)
                .OrderBy(target => target.Sequence)
                .Select(target => target.Sequence)
                .ToArrayAsync();

            Assert.Equal(commandCount * 2, contenderCount);
            Assert.Equal(Enumerable.Range(1, commandCount).Select(value => (long)value), sequences);
        }
        finally
        {
            foreach (HttpResponseMessage response in responses)
            {
                response.Dispose();
            }

            using WatcherDbContext cleanupDb = _fixture.CreateDbContext();
            await cleanupDb.Database.ExecuteSqlRawAsync("""
                DROP TRIGGER IF EXISTS wait_for_location_command_sequence_contenders ON location_command_sequences;
                DROP FUNCTION IF EXISTS wait_for_location_command_sequence_contenders();
                DROP SEQUENCE IF EXISTS location_command_sequence_test_arrivals;
                """);
        }
    }

    [PostgresFact]
    public async Task Create_replay_returns_same_command_without_duplicate_rows()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var idempotencyKey = Guid.NewGuid();

        using HttpResponseMessage first = await SendCreateAsync(
            client, seed.OperatorId, idempotencyKey, [seed.FirstLocationId]);
        using HttpResponseMessage replay = await SendCreateAsync(
            client, seed.OperatorId, idempotencyKey, [seed.FirstLocationId]);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, replay.StatusCode);
        CommandResponse? firstBody = await first.Content.ReadFromJsonAsync<CommandResponse>();
        CommandResponse? replayBody = await replay.Content.ReadFromJsonAsync<CommandResponse>();
        Assert.NotNull(firstBody);
        Assert.NotNull(replayBody);
        Assert.Equal(firstBody.Id, replayBody.Id);
        Assert.Equal(firstBody.Action, replayBody.Action);
        Assert.Equal(firstBody.PresetCode, replayBody.PresetCode);
        Assert.Equal(firstBody.Targets, replayBody.Targets);
        Assert.InRange((firstBody.CreatedAtUtc - replayBody.CreatedAtUtc).Duration(),
            TimeSpan.Zero, TimeSpan.FromTicks(10));
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.AlertCommands.CountAsync());
        Assert.Equal(1, await db.CommandTargets.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [PostgresTheory]
    [InlineData("stop", "evacuation", false)]
    [InlineData("start", "other-preset", false)]
    [InlineData("start", "evacuation", true)]
    public async Task Create_replay_with_a_changed_payload_returns_conflict_without_duplicate_rows(
        string action, string presetCode, bool useDifferentLocation)
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var idempotencyKey = Guid.NewGuid();
        using HttpResponseMessage original = await SendCreateAsync(
            client, seed.OperatorId, idempotencyKey, [seed.FirstLocationId]);
        using HttpRequestMessage changedRequest = CreateRequest(seed.OperatorId, idempotencyKey, new
        {
            action,
            presetCode,
            locationIds = new[] { useDifferentLocation ? seed.SecondLocationId : seed.FirstLocationId }
        });
        changedRequest.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, seed.OperatorId));

        using HttpResponseMessage replay = await client.SendAsync(changedRequest);

        Assert.Equal(HttpStatusCode.Accepted, original.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.AlertCommands.CountAsync());
        Assert.Equal(1, await db.CommandTargets.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [PostgresFact]
    public async Task Concurrent_retries_with_one_idempotency_key_return_the_same_command()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var key = Guid.NewGuid();
        var csrfToken = await GetCsrfTokenAsync(client, seed.OperatorId);

        Task<HttpResponseMessage> firstTask = SendCreateAsync(
            client, seed.OperatorId, key, [seed.FirstLocationId], csrfToken);
        Task<HttpResponseMessage> secondTask = SendCreateAsync(
            client, seed.OperatorId, key, [seed.FirstLocationId], csrfToken);
        using HttpResponseMessage first = await firstTask;
        using HttpResponseMessage second = await secondTask;

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        CommandResponse? firstBody = await first.Content.ReadFromJsonAsync<CommandResponse>();
        CommandResponse? secondBody = await second.Content.ReadFromJsonAsync<CommandResponse>();
        Assert.NotNull(firstBody);
        Assert.NotNull(secondBody);
        Assert.Equal(firstBody.Id, secondBody.Id);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.AlertCommands.CountAsync());
        Assert.Equal(1, await db.CommandTargets.CountAsync());
        Assert.Equal(1, await db.AuditEvents.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [PostgresFact]
    public async Task Concurrent_requests_with_one_key_and_different_payloads_conflict_once()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.SoundPresets.Add(new SoundPreset
            {
                Id = Guid.NewGuid(),
                Code = "drill",
                Name = "Навчальна перевірка",
                Priority = 2,
                ManifestHash = new string('b', 64),
                IsPublished = true
            });
            await db.SaveChangesAsync();
        }

        var key = Guid.NewGuid();
        var csrfToken = await GetCsrfTokenAsync(client, seed.OperatorId);
        Task<HttpResponseMessage> firstTask = SendCreateAsync(
            client, seed.OperatorId, key, [seed.FirstLocationId], csrfToken);
        using HttpRequestMessage changedRequest = CreateRequest(seed.OperatorId, key, new
        {
            action = "start",
            presetCode = "drill",
            locationIds = new[] { seed.FirstLocationId }
        });
        changedRequest.Headers.Add("X-CSRF-TOKEN", csrfToken);
        Task<HttpResponseMessage> secondTask = client.SendAsync(changedRequest);
        using HttpResponseMessage first = await firstTask;
        using HttpResponseMessage second = await secondTask;

        Assert.Contains(HttpStatusCode.Accepted, new[] { first.StatusCode, second.StatusCode });
        Assert.Contains(HttpStatusCode.Conflict, new[] { first.StatusCode, second.StatusCode });
        Assert.NotEqual(first.StatusCode, second.StatusCode);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        Assert.Equal(1, await readDb.AlertCommands.CountAsync());
        Assert.Equal(1, await readDb.CommandTargets.CountAsync());
        Assert.Equal(1, await readDb.AuditEvents.CountAsync());
        Assert.Equal(1, await readDb.OutboxMessages.CountAsync());
    }

    [PostgresFact]
    public async Task Create_does_not_disclose_a_command_to_another_users_idempotency_replay()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        var idempotencyKey = Guid.NewGuid();
        using HttpResponseMessage original = await SendCreateAsync(
            client, seed.OperatorId, idempotencyKey, [seed.FirstLocationId]);

        using HttpResponseMessage replay = await SendCreateAsync(
            client, seed.OtherUserId, idempotencyKey, [seed.FirstLocationId]);

        Assert.Equal(HttpStatusCode.Accepted, original.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(1, await db.AlertCommands.CountAsync());
        Assert.Equal(1, await db.OutboxMessages.CountAsync());
    }

    [PostgresTheory]
    [InlineData("stop", "evacuation", false)]
    [InlineData("start", "missing", false)]
    [InlineData("start", "evacuation", true)]
    public async Task Create_rejects_invalid_actions_presets_and_duplicate_locations(
        string action, string presetCode, bool duplicateLocations)
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        Guid[] locations = duplicateLocations
            ? [seed.FirstLocationId, seed.FirstLocationId]
            : [seed.FirstLocationId];

        using HttpRequestMessage request = CreateRequest(seed.OperatorId, Guid.NewGuid(), new
        {
            action,
            presetCode,
            locationIds = locations
        });
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, seed.OperatorId));
        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(duplicateLocations || action != "start" ? HttpStatusCode.BadRequest : HttpStatusCode.UnprocessableEntity,
            response.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Empty(await db.AlertCommands.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [PostgresTheory]
    [InlineData("[]", HttpStatusCode.BadRequest)]
    [InlineData("{\"action\":\"start\",\"presetCode\":\"evacuation\",\"locationIds\":[]}", HttpStatusCode.BadRequest)]
    [InlineData("{\"action\":\"start\",\"presetCode\":\"evacuation\",\"locationIds\":null}", HttpStatusCode.BadRequest)]
    public async Task Create_rejects_empty_or_incomplete_json_contracts(string payload, HttpStatusCode expectedStatus)
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/alert-commands")
        {
            Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
        };
        AddIdentity(request, seed.OperatorId);
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, seed.OperatorId));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(expectedStatus, response.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Empty(await db.AlertCommands.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [PostgresFact]
    public async Task Create_rejects_an_empty_idempotency_header()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using HttpRequestMessage request = CreateRequest(seed.OperatorId, Guid.Empty, new
        {
            action = "start",
            presetCode = "evacuation",
            locationIds = new[] { seed.FirstLocationId }
        });
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(client, seed.OperatorId));

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Empty(await db.AlertCommands.ToListAsync());
    }

    [PostgresFact]
    public async Task Create_rejects_a_location_outside_the_operators_scope()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();

        using HttpResponseMessage response = await SendCreateAsync(
            client, seed.OperatorId, Guid.NewGuid(), [seed.SecondLocationId]);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Empty(await db.AlertCommands.ToListAsync());
    }

    [PostgresFact]
    public async Task Create_accepts_a_global_administrator_and_persists_all_targets()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync(includeGlobalAdministrator: true);

        using HttpResponseMessage response = await SendCreateAsync(
            client, seed.AdministratorId, Guid.NewGuid(), [seed.FirstLocationId, seed.SecondLocationId]);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal(2, await db.CommandTargets.CountAsync());
        Assert.Single(await db.OutboxMessages.ToListAsync());
    }

    [PostgresFact]
    public async Task Create_rejects_inactive_locations_and_unpublished_presets()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Location location = await db.Locations.SingleAsync(item => item.Id == seed.FirstLocationId);
            location.IsActive = false;
            SoundPreset preset = await db.SoundPresets.SingleAsync();
            preset.IsPublished = false;
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage response = await SendCreateAsync(
            client, seed.OperatorId, Guid.NewGuid(), [seed.FirstLocationId]);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using WatcherDbContext readDb = _fixture.CreateDbContext();
        Assert.Empty(await readDb.AlertCommands.ToListAsync());
        Assert.Empty(await readDb.OutboxMessages.ToListAsync());
    }

    [PostgresFact]
    public async Task Get_allows_the_initiator_and_hides_a_command_from_another_user()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using HttpResponseMessage created = await SendCreateAsync(
            client, seed.OperatorId, Guid.NewGuid(), [seed.FirstLocationId]);
        CommandResponse? body = await created.Content.ReadFromJsonAsync<CommandResponse>();
        Assert.NotNull(body);

        using HttpRequestMessage ownerRequest = new(HttpMethod.Get, $"/api/v1/alert-commands/{body.Id}");
        AddIdentity(ownerRequest, seed.OperatorId);
        using HttpResponseMessage ownerResponse = await client.SendAsync(ownerRequest);
        using HttpRequestMessage otherRequest = new(HttpMethod.Get, $"/api/v1/alert-commands/{body.Id}");
        AddIdentity(otherRequest, seed.OtherUserId);
        using HttpResponseMessage otherResponse = await client.SendAsync(otherRequest);
        using HttpRequestMessage auditorRequest = new(HttpMethod.Get, $"/api/v1/alert-commands/{body.Id}");
        AddIdentity(auditorRequest, seed.OtherUserId, "auditor");
        using HttpResponseMessage auditorResponse = await client.SendAsync(auditorRequest);
        using HttpRequestMessage missingRequest = new(HttpMethod.Get,
            $"/api/v1/alert-commands/{Guid.NewGuid()}");
        AddIdentity(missingRequest, seed.OtherUserId);
        using HttpResponseMessage missingResponse = await client.SendAsync(missingRequest);

        Assert.Equal(HttpStatusCode.OK, ownerResponse.StatusCode);
        Assert.Equal("no-store", ownerResponse.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);
        Assert.Equal("no-store", otherResponse.Headers.CacheControl?.ToString());
        Assert.Equal(HttpStatusCode.NotFound, missingResponse.StatusCode);
        Assert.Equal("no-store", missingResponse.Headers.CacheControl?.ToString());
        await AssertSameNotFoundDetailsAsync(missingResponse, otherResponse);
        Assert.Equal(HttpStatusCode.OK, auditorResponse.StatusCode);
    }

    [PostgresFact]
    public async Task Get_returns_not_found_for_an_unknown_command()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using HttpRequestMessage request = new(HttpMethod.Get, $"/api/v1/alert-commands/{Guid.NewGuid()}");
        AddIdentity(request, seed.OperatorId);

        using HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [PostgresFact]
    public async Task Locations_returns_all_active_locations_to_a_global_notificator()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Role notificator = await db.Roles.SingleAsync(role => role.Code == "notificator");
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = seed.OperatorId,
                RoleId = notificator.Id,
                LocationId = null
            });
            await db.SaveChangesAsync();
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/locations");
        AddIdentity(request, seed.OperatorId);
        using HttpResponseMessage response = await client.SendAsync(request);
        LocationResponse[]? locations = await response.Content.ReadFromJsonAsync<LocationResponse[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, locations?.Length);
        Assert.Equal(new[] { "Південний корпус", "Північний корпус" }, locations?.Select(location => location.Name));
    }

    [PostgresFact]
    public async Task Locations_returns_only_active_locations_assigned_to_the_operator()
    {
        HttpClient client = RequireClient();
        SeedData seed = await _fixture.ResetAndSeedAsync();
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            Location inactiveLocation = await db.Locations.SingleAsync(item => item.Id == seed.SecondLocationId);
            inactiveLocation.IsActive = false;
            await db.SaveChangesAsync();
        }

        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/locations");
        AddIdentity(request, seed.OperatorId);
        using HttpResponseMessage response = await client.SendAsync(request);
        LocationResponse[]? locations = await response.Content.ReadFromJsonAsync<LocationResponse[]>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(seed.FirstLocationId, Assert.Single(locations!).Id);
    }

    [PostgresFact]
    public async Task Alert_commands_and_locations_require_authentication()
    {
        HttpClient client = RequireClient();
        using HttpResponseMessage create = await client.PostAsJsonAsync("/api/v1/alert-commands", new
        {
            action = "start",
            presetCode = "evacuation",
            locationIds = new[] { Guid.NewGuid() }
        });
        using HttpResponseMessage locations = await client.GetAsync("/api/v1/locations");

        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, locations.StatusCode);
    }

    private HttpClient RequireClient() => _fixture.Client;

    private static async Task<HttpResponseMessage> SendCreateAsync(
        HttpClient client, Guid userId, Guid idempotencyKey, Guid[] locationIds, string? csrfToken = null)
    {
        using HttpRequestMessage request = CreateRequest(userId, idempotencyKey, new
        {
            action = "start",
            presetCode = "evacuation",
            locationIds
        });
        csrfToken ??= await GetCsrfTokenAsync(client, userId);
        request.Headers.Add("X-CSRF-TOKEN", csrfToken);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetCsrfTokenAsync(HttpClient client, Guid userId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        AddIdentity(request, userId);
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = System.Text.Json.JsonDocument.Parse(
            await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()
            ?? throw new InvalidOperationException("API не повернув antiforgery token.");
    }

    private static HttpRequestMessage CreateRequest(Guid userId, Guid idempotencyKey, object content)
    {
        HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/alert-commands")
        {
            Content = JsonContent.Create(content)
        };
        AddIdentity(request, userId);
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        return request;
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

    private static void AddIdentity(HttpRequestMessage request, Guid userId, string? role = null)
    {
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        if (role is not null)
        {
            request.Headers.Add("X-Test-Role", role);
        }
    }

    private sealed record CommandResponse(
        Guid Id,
        string Action,
        string PresetCode,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset ExpiresAtUtc,
        TargetResponse[] Targets);
    private sealed record TargetResponse(Guid LocationId, string State);
    private sealed record LocationResponse(Guid Id, string Name, string Code);
}

public sealed class PostgresApiFixture : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Watcher");
    private readonly string _schema = $"api_test_{Guid.NewGuid():N}";
    private string? _isolatedConnectionString;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public HttpClient Client => _client ??= _factory?.CreateClient() ?? throw new InvalidOperationException(
        "PostgreSQL fixture is unavailable; the test should be skipped without ConnectionStrings__Watcher.");

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        NpgsqlConnectionStringBuilder connection = new(_connectionString) { SearchPath = _schema };
        _isolatedConnectionString = connection.ConnectionString;
        await using (NpgsqlConnection adminConnection = new(_connectionString))
        {
            await adminConnection.OpenAsync();
            await using NpgsqlCommand createSchema = new($"CREATE SCHEMA \"{_schema}\"", adminConnection);
            await createSchema.ExecuteNonQueryAsync();
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Watcher"] = _isolatedConnectionString,
                    ["Authentication:Google:Enabled"] = "false"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<WatcherDbContext>();
                services.RemoveAll<DbContextOptions<WatcherDbContext>>();
                services.AddDbContext<WatcherDbContext>(options => options.UseNpgsql(_isolatedConnectionString));
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            });
        });

        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost")
        });

        using IServiceScope scope = _factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        await db.Database.MigrateAsync();

        var activeSchema = await db.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"")
            .SingleAsync();
        if (!string.Equals(activeSchema, _schema, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Міграції інтеграційного тесту застосовані не до ізольованої схеми.");
        }

        var outboxExists = await db.Database.SqlQueryRaw<bool>(
            "SELECT to_regclass('outbox_messages') IS NOT NULL AS \"Value\"").SingleAsync();
        if (!outboxExists)
        {
            throw new InvalidOperationException("Ізольована схема не містить таблиці outbox після застосування міграцій.");
        }
    }

    public WatcherDbContext CreateDbContext(params IInterceptor[] interceptors)
    {
        if (_isolatedConnectionString is null)
        {
            throw new InvalidOperationException("PostgreSQL fixture is unavailable.");
        }

        DbContextOptionsBuilder<WatcherDbContext> optionsBuilder = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql(_isolatedConnectionString);
        if (interceptors.Length > 0)
        {
            optionsBuilder.AddInterceptors(interceptors);
        }

        DbContextOptions<WatcherDbContext> options = optionsBuilder.Options;
        return new WatcherDbContext(options);
    }

    public async Task<DateTimeOffset> MigrateLegacyCommandExpiryAsync(DateTimeOffset createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("PostgreSQL fixture is unavailable.");
        }

        var schema = $"expiry_test_{Guid.NewGuid():N}";
        NpgsqlConnectionStringBuilder connection = new(_connectionString) { SearchPath = schema };
        await using (NpgsqlConnection adminConnection = new(_connectionString))
        {
            await adminConnection.OpenAsync();
            await using NpgsqlCommand createSchema = new($"CREATE SCHEMA \"{schema}\"", adminConnection);
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .Options;
            await using WatcherDbContext db = new(options);
            IMigrator migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261105004326_AddMinuteOfSilenceScheduleAndSystemActor");

            var commandId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO alert_commands
                    ("Id", "IdempotencyKey", "Action", "PresetCode", "InitiatedByUserId", "CreatedAtUtc")
                VALUES
                    ({commandId}, {Guid.NewGuid()}, {"start"}, {"minute_of_silence"}, {null}, {createdAtUtc})
                """);

            await migrator.MigrateAsync();
            return await db.Database.SqlQuery<DateTimeOffset>($"""
                SELECT "ExpiresAtUtc" AS "Value"
                FROM alert_commands
                WHERE "Id" = {commandId}
                """).SingleAsync();
        }
        finally
        {
            await using NpgsqlConnection adminConnection = new(_connectionString);
            await adminConnection.OpenAsync();
            await using NpgsqlCommand dropSchema = new($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    public async Task<(
        long FirstCommandSequence,
        long SecondCommandSequence,
        long FirstLocationNextSequence,
        long SecondLocationSequence,
        long SecondLocationNextSequence,
        long LegacyBinarySequence,
        long FirstLocationNextSequenceAfterLegacyInsert)> MigrateLegacyTargetSequencesAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            throw new InvalidOperationException("PostgreSQL fixture is unavailable.");
        }

        var schema = $"sequence_test_{Guid.NewGuid():N}";
        NpgsqlConnectionStringBuilder connection = new(_connectionString) { SearchPath = schema };
        await using (NpgsqlConnection adminConnection = new(_connectionString))
        {
            await adminConnection.OpenAsync();
            await using NpgsqlCommand createSchema = new($"CREATE SCHEMA \"{schema}\"", adminConnection);
            await createSchema.ExecuteNonQueryAsync();
        }

        try
        {
            DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
                .UseNpgsql(connection.ConnectionString)
                .Options;
            await using WatcherDbContext db = new(options);
            IMigrator migrator = db.Database.GetService<IMigrator>();
            await migrator.MigrateAsync("20261105010555_AddCommandExpiry");

            var locationId = Guid.NewGuid();
            var secondLocationId = Guid.NewGuid();
            var firstCommandId = Guid.NewGuid();
            var secondCommandId = Guid.NewGuid();
            DateTimeOffset firstCreatedAt = new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
            DateTimeOffset secondCreatedAt = firstCreatedAt.AddMinutes(1);
            DateTimeOffset locationCreatedAt = firstCreatedAt.AddDays(-1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO locations ("Id", "Code", "Name", "IsActive", "CreatedAtUtc")
                VALUES
                    ({locationId}, {"migration-test-1"}, {"Тестова локація 1"}, {true}, {locationCreatedAt}),
                    ({secondLocationId}, {"migration-test-2"}, {"Тестова локація 2"}, {true}, {locationCreatedAt});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO alert_commands
                    ("Id", "IdempotencyKey", "Action", "PresetCode", "InitiatedByUserId", "CreatedAtUtc", "ExpiresAtUtc")
                VALUES
                    ({firstCommandId}, {Guid.NewGuid()}, {"start"}, {"test"}, {null}, {firstCreatedAt}, {firstCreatedAt.AddMinutes(5)}),
                    ({secondCommandId}, {Guid.NewGuid()}, {"start"}, {"test"}, {null}, {secondCreatedAt}, {secondCreatedAt.AddMinutes(5)});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO command_targets ("Id", "CommandId", "LocationId", "State")
                VALUES
                    ({Guid.NewGuid()}, {secondCommandId}, {locationId}, {"Created"}),
                    ({Guid.NewGuid()}, {firstCommandId}, {locationId}, {"Created"}),
                    ({Guid.NewGuid()}, {firstCommandId}, {secondLocationId}, {"Created"});
                """);

            await migrator.MigrateAsync();
            var firstCommandSequence = await db.Database.SqlQuery<long>($"""
                SELECT "Sequence" AS "Value"
                FROM command_targets
                WHERE "CommandId" = {firstCommandId} AND "LocationId" = {locationId}
                """).SingleAsync();
            var secondCommandSequence = await db.Database.SqlQuery<long>($"""
                SELECT "Sequence" AS "Value"
                FROM command_targets
                WHERE "CommandId" = {secondCommandId} AND "LocationId" = {locationId}
                """).SingleAsync();
            var firstLocationNextSequence = await db.Database.SqlQuery<long>($"""
                SELECT "NextSequence" AS "Value"
                FROM location_command_sequences
                WHERE "LocationId" = {locationId}
                """).SingleAsync();
            var secondLocationSequence = await db.Database.SqlQuery<long>($"""
                SELECT "Sequence" AS "Value"
                FROM command_targets
                WHERE "CommandId" = {firstCommandId} AND "LocationId" = {secondLocationId}
                """).SingleAsync();
            var secondLocationNextSequence = await db.Database.SqlQuery<long>($"""
                SELECT "NextSequence" AS "Value"
                FROM location_command_sequences
                WHERE "LocationId" = {secondLocationId}
                """).SingleAsync();
            var legacyCommandId = Guid.NewGuid();
            DateTimeOffset legacyCreatedAt = secondCreatedAt.AddMinutes(1);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO alert_commands
                    ("Id", "IdempotencyKey", "Action", "PresetCode", "InitiatedByUserId", "CreatedAtUtc", "ExpiresAtUtc")
                VALUES
                    ({legacyCommandId}, {Guid.NewGuid()}, {"start"}, {"test"}, {null}, {legacyCreatedAt}, {legacyCreatedAt.AddMinutes(5)});
                """);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO command_targets ("Id", "CommandId", "LocationId", "State")
                VALUES ({Guid.NewGuid()}, {legacyCommandId}, {locationId}, {"Created"});
                """);
            var legacyBinarySequence = await db.Database.SqlQuery<long>($"""
                SELECT "Sequence" AS "Value"
                FROM command_targets
                WHERE "CommandId" = {legacyCommandId} AND "LocationId" = {locationId}
                """).SingleAsync();
            var firstLocationNextSequenceAfterLegacyInsert = await db.Database.SqlQuery<long>($"""
                SELECT "NextSequence" AS "Value"
                FROM location_command_sequences
                WHERE "LocationId" = {locationId}
                """).SingleAsync();

            return (firstCommandSequence, secondCommandSequence, firstLocationNextSequence,
                secondLocationSequence, secondLocationNextSequence, legacyBinarySequence,
                firstLocationNextSequenceAfterLegacyInsert);
        }
        finally
        {
            await using NpgsqlConnection adminConnection = new(_connectionString);
            await adminConnection.OpenAsync();
            await using NpgsqlCommand dropSchema = new($"DROP SCHEMA IF EXISTS \"{schema}\" CASCADE", adminConnection);
            await dropSchema.ExecuteNonQueryAsync();
        }
    }

    public async Task<SeedData> ResetAndSeedAsync(bool includeGlobalAdministrator = false)
    {
        using WatcherDbContext db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE command_deliveries, outbox_messages, audit_events, command_targets, alert_commands, " +
            "device_sessions, devices, " +
            "user_roles, roles, sound_presets, locations, users CASCADE");

        var seed = SeedData.Create(includeGlobalAdministrator);
        db.Users.AddRange(
            new UserAccount { Id = seed.OperatorId, ExternalSubject = "operator", Email = "operator@example.test" },
            new UserAccount { Id = seed.OtherUserId, ExternalSubject = "other", Email = "other@example.test" });
        db.Locations.AddRange(
            new Location { Id = seed.FirstLocationId, Code = "north", Name = "Північний корпус" },
            new Location { Id = seed.SecondLocationId, Code = "south", Name = "Південний корпус" });
        db.SoundPresets.Add(new SoundPreset
        {
            Id = Guid.NewGuid(),
            Code = "evacuation",
            Name = "Евакуація",
            Priority = 1,
            ManifestHash = new string('a', 64),
            IsPublished = true
        });
        Role notificator = new() { Id = Guid.NewGuid(), Code = "notificator", Name = "Оператор" };
        db.Roles.Add(notificator);
        db.UserRoles.Add(new UserRole
        {
            Id = Guid.NewGuid(),
            UserId = seed.OperatorId,
            RoleId = notificator.Id,
            LocationId = seed.FirstLocationId
        });

        if (includeGlobalAdministrator)
        {
            db.Users.Add(new UserAccount
            {
                Id = seed.AdministratorId,
                ExternalSubject = "admin",
                Email = "admin@example.test"
            });
            Role administrator = new() { Id = Guid.NewGuid(), Code = "admin", Name = "Адміністратор" };
            db.Roles.Add(administrator);
            db.UserRoles.Add(new UserRole
            {
                Id = Guid.NewGuid(),
                UserId = seed.AdministratorId,
                RoleId = administrator.Id
            });
        }

        await db.SaveChangesAsync();
        return seed;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }

        if (_connectionString is null)
        {
            return;
        }

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand dropSchema = new($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await dropSchema.ExecuteNonQueryAsync();
    }
}

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__Watcher")))
        {
            Skip = "Потрібен локальний PostgreSQL. У CI інтеграційна база налаштована автоматично.";
        }
    }
}

public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ConnectionStrings__Watcher")))
        {
            Skip = "Потрібен локальний PostgreSQL. У CI інтеграційна база налаштована автоматично.";
        }
    }
}

public sealed record SeedData(
    Guid OperatorId,
    Guid OtherUserId,
    Guid AdministratorId,
    Guid FirstLocationId,
    Guid SecondLocationId)
{
    public static SeedData Create(bool includeGlobalAdministrator) => new(
        Guid.NewGuid(), Guid.NewGuid(), includeGlobalAdministrator ? Guid.NewGuid() : Guid.Empty,
        Guid.NewGuid(), Guid.NewGuid());
}

public sealed class TestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-Test-User-Id", out Microsoft.Extensions.Primitives.StringValues userId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        Claim[] claims = [new Claim("kgta_user_id", userId.ToString())];
        if (Request.Headers.TryGetValue("X-Test-Role", out Microsoft.Extensions.Primitives.StringValues roles))
        {
            foreach (var role in roles)
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    claims = [.. claims, new Claim(ClaimTypes.Role, role)];
                }
            }
        }
        ClaimsIdentity identity = new(claims, SchemeName);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal static class AuditPayloadExtensions
{
    public static Guid DeserializeCommandId(this string payload) =>
        System.Text.Json.JsonDocument.Parse(payload).RootElement.GetProperty("Id").GetGuid();
}
