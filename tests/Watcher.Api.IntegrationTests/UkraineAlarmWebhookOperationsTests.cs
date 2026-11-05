using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Watcher.Api.AlertSources;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class UkraineAlarmWebhookOperationsTests : IClassFixture<WebhookOperationsFixture>
{
    private const string OperationsPath = "/api/v1/admin/integrations/ukrainealarm/webhook-operations";
    private readonly WebhookOperationsFixture _fixture;

    public UkraineAlarmWebhookOperationsTests(WebhookOperationsFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Registration_is_audited_and_same_idempotency_key_never_calls_provider_twice()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage reconciled = await SendReconcileStateAsync(administratorId, false);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        var key = Guid.NewGuid();

        using HttpResponseMessage first = await SendStartAsync(administratorId, key, "register");
        using HttpResponseMessage replay = await SendStartAsync(administratorId, key, "register");
        using HttpResponseMessage changedReplay = await SendStartAsync(administratorId, key, "update");

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, changedReplay.StatusCode);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());
        JsonElement firstBody = await ReadBodyAsync(first);
        JsonElement replayBody = await ReadBodyAsync(replay);
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), replayBody.GetProperty("id").GetGuid());
        Assert.Equal("Succeeded", replayBody.GetProperty("state").GetString());
        Assert.Equal("https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook",
            replayBody.GetProperty("callbackUrl").GetString());
        Assert.DoesNotContain("test-token", await replay.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "register" }, _fixture.Registrar.Calls.Select(call => call.Action));
        Assert.Equal("https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook",
            Assert.Single(_fixture.Registrar.Calls).Url.AbsoluteUri);

        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.True((await db.ExternalIntegrationStates.SingleAsync()).IsRegistered);
        UkraineAlarmWebhookOperation operation = await db.UkraineAlarmWebhookOperations.SingleAsync();
        Assert.Equal(administratorId, operation.ActorUserId);
        Assert.Equal("Succeeded", operation.State);
        Assert.Equal(3, await db.AuditEvents.CountAsync());
        Assert.DoesNotContain("test-token", string.Join(' ', await db.AuditEvents.Select(item => item.Payload).ToArrayAsync()));
    }

    [PostgresFact]
    public async Task Indeterminate_result_blocks_new_operation_until_admin_confirms_provider_state()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage reconciled = await SendReconcileStateAsync(administratorId, false);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        _fixture.Registrar.ResponseStatusCode = HttpStatusCode.InternalServerError;

        using HttpResponseMessage indeterminate = await SendStartAsync(administratorId, Guid.NewGuid(), "register");
        using var indeterminateBody = JsonDocument.Parse(await indeterminate.Content.ReadAsStringAsync());
        Guid operationId = indeterminateBody.RootElement.GetProperty("id").GetGuid();
        using HttpResponseMessage blocked = await SendStartAsync(administratorId, Guid.NewGuid(), "register");

        Assert.Equal(HttpStatusCode.Accepted, indeterminate.StatusCode);
        Assert.Equal("Unknown", indeterminateBody.RootElement.GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Single(_fixture.Registrar.Calls);

        using HttpResponseMessage unresolved = await ResolveAsync(administratorId, operationId, confirmed: false,
            isRegistered: false);
        Assert.Equal(HttpStatusCode.BadRequest, unresolved.StatusCode);
        using HttpResponseMessage resolved = await ResolveAsync(administratorId, operationId, confirmed: true,
            isRegistered: false);
        Assert.Equal(HttpStatusCode.OK, resolved.StatusCode);

        _fixture.Registrar.ResponseStatusCode = HttpStatusCode.NoContent;
        using HttpResponseMessage retry = await SendStartAsync(administratorId, Guid.NewGuid(), "register");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(new[] { "register", "register" }, _fixture.Registrar.Calls.Select(call => call.Action));
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Equal("Succeeded", await db.UkraineAlarmWebhookOperations
            .OrderByDescending(item => item.CreatedAtUtc).Select(item => item.State).FirstAsync());
        Assert.True((await db.ExternalIntegrationStates.SingleAsync()).IsRegistered);
    }

    [PostgresFact]
    public async Task Update_requires_known_registration_and_only_global_admin_can_start_operations()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage updateBeforeRegistration = await SendStartAsync(
            administratorId, Guid.NewGuid(), "update");
        using HttpResponseMessage registerBeforeReconciliation = await SendStartAsync(
            administratorId, Guid.NewGuid(), "register");
        Assert.Equal(HttpStatusCode.Conflict, updateBeforeRegistration.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, registerBeforeReconciliation.StatusCode);

        Guid auditorId = await _fixture.AddUserWithRoleAsync("auditor", global: true);
        Guid localAdministratorId = await _fixture.AddUserWithRoleAsync("admin", global: false);
        Guid notificatorId = await _fixture.AddUserWithRoleAsync("notificator", global: false);
        using HttpResponseMessage auditor = await SendStartAsync(auditorId, Guid.NewGuid(), "register");
        using HttpResponseMessage localAdministrator = await SendStartAsync(
            localAdministratorId, Guid.NewGuid(), "register");
        using HttpResponseMessage notificator = await SendStartAsync(notificatorId, Guid.NewGuid(), "register");
        using HttpResponseMessage anonymous = await _fixture.Client.PostAsJsonAsync(
            OperationsPath, new { action = "register" });

        Assert.Equal(HttpStatusCode.Forbidden, auditor.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, localAdministrator.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notificator.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Empty(_fixture.Registrar.Calls);
    }

    [PostgresFact]
    public async Task Existing_provider_registration_can_be_reconciled_before_update()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage initialState = await SendGetStateAsync(administratorId);
        using var initialBody = JsonDocument.Parse(await initialState.Content.ReadAsStringAsync());
        Assert.Equal("unknown", initialBody.RootElement.GetProperty("state").GetString());

        using HttpResponseMessage reconciled = await SendReconcileStateAsync(administratorId, true);
        using var reconciledBody = JsonDocument.Parse(await reconciled.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        Assert.Equal("registered", reconciledBody.RootElement.GetProperty("state").GetString());

        using HttpResponseMessage update = await SendStartAsync(administratorId, Guid.NewGuid(), "update");
        Assert.Equal(HttpStatusCode.Accepted, update.StatusCode);
        Assert.Equal(new[] { "update" }, _fixture.Registrar.Calls.Select(call => call.Action));
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Contains(await db.AuditEvents.ToArrayAsync(), item =>
            item.EventType == "integration.ukrainealarm_webhook.state_reconciled");
    }

    [PostgresFact]
    public async Task Missing_csrf_is_rejected_before_provider_call_and_request_is_not_audited()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpRequestMessage request = new(HttpMethod.Post, OperationsPath)
        {
            Content = JsonContent.Create(new { action = "register" })
        };
        request.Headers.Add("X-Test-User-Id", administratorId.ToString());
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using HttpResponseMessage response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_fixture.Registrar.Calls);
        using WatcherDbContext db = _fixture.CreateDbContext();
        Assert.Empty(await db.AuditEvents.ToArrayAsync());
        Assert.Empty(await db.UkraineAlarmWebhookOperations.ToArrayAsync());
    }

    [PostgresFact]
    public async Task Concurrent_request_is_rejected_while_one_operation_is_running()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage reconciled = await SendReconcileStateAsync(administratorId, false);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        _fixture.Registrar.ReleaseCall = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<HttpResponseMessage> firstTask = SendStartAsync(administratorId, Guid.NewGuid(), "register");
        await _fixture.Registrar.CallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        using HttpResponseMessage second = await SendStartAsync(administratorId, Guid.NewGuid(), "register");
        _fixture.Registrar.ReleaseCall.SetResult();
        using HttpResponseMessage first = await firstTask;

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Single(_fixture.Registrar.Calls);
    }

    [PostgresFact]
    public async Task Provider_rejection_is_terminal_and_can_be_retried_with_a_new_key()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage reconciled = await SendReconcileStateAsync(administratorId, false);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);
        _fixture.Registrar.ResponseStatusCode = HttpStatusCode.BadRequest;

        using HttpResponseMessage rejected = await SendStartAsync(administratorId, Guid.NewGuid(), "register");
        using var rejectedBody = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadGateway, rejected.StatusCode);
        Guid rejectedId = rejectedBody.RootElement.GetProperty("operationId").GetGuid();
        using HttpResponseMessage rejectedStatus = await SendGetOperationAsync(administratorId, rejectedId);
        Assert.Equal(HttpStatusCode.OK, rejectedStatus.StatusCode);
        using var statusBody = JsonDocument.Parse(await rejectedStatus.Content.ReadAsStringAsync());
        Assert.Equal("Rejected", statusBody.RootElement.GetProperty("state").GetString());

        _fixture.Registrar.ResponseStatusCode = HttpStatusCode.NoContent;
        using HttpResponseMessage retry = await SendStartAsync(administratorId, Guid.NewGuid(), "register");
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        Assert.Equal(2, _fixture.Registrar.Calls.Count);
    }

    [PostgresTheory]
    [InlineData("Pending")]
    [InlineData("Running")]
    public async Task Active_operation_cannot_be_manually_resolved_while_a_handler_may_still_be_running(string state)
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        var operationId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        using (WatcherDbContext db = _fixture.CreateDbContext())
        {
            db.UkraineAlarmWebhookOperations.Add(new UkraineAlarmWebhookOperation
            {
                Id = operationId,
                IdempotencyKey = Guid.NewGuid(),
                ActorUserId = administratorId,
                Action = "register",
                State = state,
                CallbackUrl = "https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            await db.SaveChangesAsync();
        }

        using HttpResponseMessage tooSoon = await ResolveAsync(
            administratorId, operationId, confirmed: true, isRegistered: true);
        Assert.Equal(HttpStatusCode.Conflict, tooSoon.StatusCode);

        using HttpResponseMessage resolved = await ResolveAsync(
            administratorId, operationId, confirmed: true, isRegistered: true);
        Assert.Equal(HttpStatusCode.Conflict, resolved.StatusCode);
        using WatcherDbContext verifyDb = _fixture.CreateDbContext();
        Assert.Equal(state, (await verifyDb.UkraineAlarmWebhookOperations.SingleAsync()).State);
        Assert.Null((await verifyDb.ExternalIntegrationStates.SingleAsync()).IsRegistered);
    }

    [PostgresFact]
    public async Task Concurrent_replay_with_same_key_returns_the_existing_operation()
    {
        Guid administratorId = await _fixture.ResetAndSeedAsync("admin", global: true);
        using HttpResponseMessage reconciled = await SendReconcileStateAsync(administratorId, false);
        Assert.Equal(HttpStatusCode.OK, reconciled.StatusCode);

        var key = Guid.NewGuid();
        _fixture.IdempotencyLookupBarrier.Arm();
        Task<HttpResponseMessage> firstTask = SendStartAsync(administratorId, key, "register");
        Task<HttpResponseMessage> secondTask = SendStartAsync(administratorId, key, "register");
        await _fixture.IdempotencyLookupBarrier.WaitUntilBothLookupsAsync();
        _fixture.IdempotencyLookupBarrier.Release();

        using HttpResponseMessage first = await firstTask;
        using HttpResponseMessage second = await secondTask;
        Assert.Contains(first.StatusCode, new[] { HttpStatusCode.Accepted, HttpStatusCode.OK });
        Assert.Contains(second.StatusCode, new[] { HttpStatusCode.Accepted, HttpStatusCode.OK });
        JsonElement firstBody = await ReadBodyAsync(first);
        JsonElement secondBody = await ReadBodyAsync(second);
        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());
        Assert.Single(_fixture.Registrar.Calls);
    }

    private async Task<HttpResponseMessage> SendStartAsync(Guid userId, Guid idempotencyKey, string action)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, OperationsPath)
        {
            Content = JsonContent.Create(new { action })
        };
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(userId));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> ResolveAsync(
        Guid userId,
        Guid operationId,
        bool confirmed,
        bool isRegistered)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{OperationsPath}/{operationId}/resolve")
        {
            Content = JsonContent.Create(new { confirmed, isRegistered })
        };
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(userId));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendGetStateAsync(Guid userId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{OperationsPath}/state");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendGetOperationAsync(Guid userId, Guid operationId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, $"{OperationsPath}/{operationId}");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendReconcileStateAsync(Guid userId, bool isRegistered)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, $"{OperationsPath}/state/reconcile")
        {
            Content = JsonContent.Create(new { confirmed = true, isRegistered })
        };
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        request.Headers.Add("X-CSRF-TOKEN", await GetCsrfTokenAsync(userId));
        return await _fixture.Client.SendAsync(request);
    }

    private async Task<string> GetCsrfTokenAsync(Guid userId)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/auth/csrf-token");
        request.Headers.Add("X-Test-User-Id", userId.ToString());
        using HttpResponseMessage response = await _fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task<JsonElement> ReadBodyAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }
}

public sealed class WebhookOperationsFixture : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Watcher");
    private readonly string _schema = $"webhook_test_{Guid.NewGuid():N}";
    private string? _isolatedConnectionString;
    private WebApplicationFactory<Program>? _factory;
    private HttpClient? _client;

    public RecordingUkraineAlarmWebhookRegistrar Registrar { get; } = new();
    public IdempotencyLookupBarrier IdempotencyLookupBarrier { get; } = new();
    public HttpClient Client => _client ?? throw new InvalidOperationException("Інтеграційний тест потребує PostgreSQL.");

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
                    ["Authentication:Google:Enabled"] = "false",
                    ["CommandOutboxWorker:Enabled"] = "false",
                    ["DeviceCommandTransport:Enabled"] = "false",
                    ["UkraineAlarm:Token"] = "test-token",
                    ["UkraineAlarm:PublicBaseUrl"] = "https://alerts.example.test"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<WatcherDbContext>();
                services.RemoveAll<DbContextOptions<WatcherDbContext>>();
                services.AddDbContext<WatcherDbContext>(options => options
                    .UseNpgsql(_isolatedConnectionString)
                    .AddInterceptors(IdempotencyLookupBarrier));
                services.RemoveAll<IUkraineAlarmWebhookRegistrar>();
                services.AddSingleton<IUkraineAlarmWebhookRegistrar>(Registrar);
                services.AddAuthentication(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                }).AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                    TestAuthenticationHandler.SchemeName, _ => { });
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost") });

        using IServiceScope scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<WatcherDbContext>().Database.MigrateAsync();
    }

    public async Task<Guid> ResetAndSeedAsync(string roleCode, bool global)
    {
        Registrar.Reset();
        using WatcherDbContext db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE audit_events, ukrainealarm_webhook_operations, external_integration_states, user_roles, roles, locations, users CASCADE");
        var userId = Guid.NewGuid();
        Role? existingRole = await db.Roles.SingleOrDefaultAsync(item => item.Code == roleCode);
        Guid roleId = existingRole?.Id ?? Guid.NewGuid();
        db.Users.Add(new UserAccount { Id = userId, ExternalSubject = $"test-{userId:N}", Email = $"{userId:N}@example.test" });
        if (existingRole is null)
        {
            db.Roles.Add(new Role { Id = roleId, Code = roleCode, Name = roleCode });
        }
        Guid? locationId = global ? null : Guid.NewGuid();
        if (locationId.HasValue)
        {
            db.Locations.Add(new Location { Id = locationId.Value, Code = $"loc-{userId:N}", Name = "Тестовий корпус" });
        }

        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, LocationId = locationId });
        db.ExternalIntegrationStates.Add(new ExternalIntegrationState
        {
            IntegrationCode = UkraineAlarmWebhookOperation.DefaultIntegrationCode,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return userId;
    }

    public async Task<Guid> AddUserWithRoleAsync(string roleCode, bool global)
    {
        using WatcherDbContext db = CreateDbContext();
        var userId = Guid.NewGuid();
        Role? existingRole = await db.Roles.SingleOrDefaultAsync(item => item.Code == roleCode);
        Guid roleId = existingRole?.Id ?? Guid.NewGuid();
        Guid? locationId = global ? null : Guid.NewGuid();
        db.Users.Add(new UserAccount { Id = userId, ExternalSubject = $"test-{userId:N}", Email = $"{userId:N}@example.test" });
        if (existingRole is null)
        {
            db.Roles.Add(new Role { Id = roleId, Code = roleCode, Name = roleCode });
        }
        if (locationId.HasValue)
        {
            db.Locations.Add(new Location { Id = locationId.Value, Code = $"loc-{userId:N}", Name = "Тестовий корпус" });
        }

        db.UserRoles.Add(new UserRole { Id = Guid.NewGuid(), UserId = userId, RoleId = roleId, LocationId = locationId });
        await db.SaveChangesAsync();
        return userId;
    }

    public WatcherDbContext CreateDbContext()
    {
        DbContextOptions<WatcherDbContext> options = new DbContextOptionsBuilder<WatcherDbContext>()
            .UseNpgsql(_isolatedConnectionString)
            .AddInterceptors(IdempotencyLookupBarrier)
            .Options;
        return new WatcherDbContext(options);
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

public sealed class IdempotencyLookupBarrier : DbCommandInterceptor
{
    private int _capturedLookups;
    private volatile bool _armed;
    private readonly TaskCompletionSource _bothLookups = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Arm() => _armed = true;

    public Task WaitUntilBothLookupsAsync() => _bothLookups.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public void Release() => _release.TrySetResult();

    public override async ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        if (_armed && command.CommandText.Contains("ukrainealarm_webhook_operations", StringComparison.OrdinalIgnoreCase) &&
            command.CommandText.Contains("IdempotencyKey", StringComparison.OrdinalIgnoreCase) &&
            Interlocked.Increment(ref _capturedLookups) <= 2)
        {
            if (Volatile.Read(ref _capturedLookups) == 2)
            {
                _bothLookups.TrySetResult();
            }

            await _release.Task.WaitAsync(cancellationToken);
            if (Volatile.Read(ref _capturedLookups) >= 2)
            {
                _armed = false;
            }
        }

        return result;
    }
}

public sealed class RecordingUkraineAlarmWebhookRegistrar : IUkraineAlarmWebhookRegistrar
{
    private readonly ConcurrentQueue<(string Action, Uri Url)> _calls = new();
    private TaskCompletionSource _callStarted = NewCompletionSource();
    private HttpStatusCode _responseStatusCode = HttpStatusCode.NoContent;
    private TaskCompletionSource? _releaseCall;

    public IReadOnlyList<(string Action, Uri Url)> Calls => _calls.ToArray();
    public TaskCompletionSource CallStarted => _callStarted;
    public TaskCompletionSource? ReleaseCall { get => _releaseCall; set => _releaseCall = value; }
    public HttpStatusCode ResponseStatusCode { get => _responseStatusCode; set => _responseStatusCode = value; }

    public Task RegisterWebhookAsync(Uri webhookUrl, CancellationToken cancellationToken) =>
        RecordAsync("register", webhookUrl, cancellationToken);

    public Task UpdateWebhookAsync(Uri webhookUrl, CancellationToken cancellationToken) =>
        RecordAsync("update", webhookUrl, cancellationToken);

    public void Reset()
    {
        _calls.Clear();
        _callStarted = NewCompletionSource();
        _releaseCall = null;
        _responseStatusCode = HttpStatusCode.NoContent;
    }

    private async Task RecordAsync(string action, Uri url, CancellationToken cancellationToken)
    {
        _calls.Enqueue((action, url));
        _callStarted.TrySetResult();
        if (_releaseCall is not null)
        {
            await _releaseCall.Task.WaitAsync(cancellationToken);
        }

        if ((int)_responseStatusCode is < 200 or >= 300)
        {
            throw new UkraineAlarmApiException(_responseStatusCode, "request_failed");
        }
    }

    private static TaskCompletionSource NewCompletionSource() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
