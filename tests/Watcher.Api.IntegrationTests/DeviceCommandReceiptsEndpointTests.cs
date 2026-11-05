using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using Watcher.Contracts.DeviceCommands;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.IntegrationTests;

public sealed class DeviceCommandReceiptsEndpointTests : IClassFixture<DeviceReceiptApiFixture>
{
    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private readonly DeviceReceiptApiFixture _fixture;

    public DeviceCommandReceiptsEndpointTests(DeviceReceiptApiFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task Receipt_endpoint_requires_device_certificate_authentication()
    {
        using HttpRequestMessage request = CreateRequest(CreateReceipt(Guid.NewGuid()));
        request.Headers.Add("X-Test-User-Id", Guid.NewGuid().ToString());
        request.Headers.Add("X-Test-Role", "admin");

        using HttpResponseMessage response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [PostgresFact]
    public async Task Receipt_endpoint_returns_202_for_recorded_and_200_for_duplicate()
    {
        ReceiptCommandSeed seed = await _fixture.CreateCommandAsync();
        CommandReceipt receipt = CreateReceipt(seed.CommandId, seed.Sequence);

        using HttpResponseMessage recorded = await _fixture.Client.SendAsync(CreateAuthenticatedRequest(receipt));
        using HttpResponseMessage duplicate = await _fixture.Client.SendAsync(CreateAuthenticatedRequest(receipt));

        Assert.Equal(HttpStatusCode.Accepted, recorded.StatusCode);
        AssertNoStore(recorded);
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        AssertNoStore(duplicate);
    }

    [PostgresFact]
    public async Task Receipt_endpoint_returns_no_store_not_found_for_an_unknown_command()
    {
        using HttpResponseMessage response = await _fixture.Client.SendAsync(
            CreateAuthenticatedRequest(CreateReceipt(Guid.NewGuid())));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        AssertNoStore(response);
    }

    [PostgresFact]
    public async Task Receipt_endpoint_returns_conflict_for_a_wrong_sequence()
    {
        ReceiptCommandSeed seed = await _fixture.CreateCommandAsync();
        CommandReceipt receipt = CreateReceipt(seed.CommandId, seed.Sequence + 1);

        using HttpResponseMessage response = await _fixture.Client.SendAsync(CreateAuthenticatedRequest(receipt));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        AssertNoStore(response);
    }

    [PostgresFact]
    public async Task Receipt_endpoint_returns_bad_request_for_an_invalid_contract()
    {
        CommandReceipt receipt = CreateReceipt(Guid.NewGuid()) with { SchemaVersion = 2 };

        using HttpResponseMessage response = await _fixture.Client.SendAsync(CreateAuthenticatedRequest(receipt));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertNoStore(response);
    }

    [PostgresFact]
    public async Task Receipt_endpoint_accepts_only_json_content()
    {
        using HttpRequestMessage request = CreateAuthenticatedRequest(CreateReceipt(Guid.NewGuid()));
        request.Content!.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

        using HttpResponseMessage response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [PostgresFact]
    public async Task Receipt_endpoint_rejects_a_body_over_8_kibibytes()
    {
        var oversizedJson = "{}" + new string(' ', 8 * 1024);
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/device/command-receipts")
        {
            Content = new StringContent(oversizedJson, System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Test-Client-Certificate", "trusted");

        using HttpResponseMessage response = await _fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    private static HttpRequestMessage CreateAuthenticatedRequest(CommandReceipt receipt)
    {
        HttpRequestMessage request = CreateRequest(receipt);
        request.Headers.Add("X-Test-Client-Certificate", "trusted");
        return request;
    }

    private static HttpRequestMessage CreateRequest(CommandReceipt receipt) =>
        new(HttpMethod.Post, "/api/v1/device/command-receipts")
        {
            Content = JsonContent.Create(receipt)
        };

    private static void AssertNoStore(HttpResponseMessage response) =>
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.OrdinalIgnoreCase);

    private static CommandReceipt CreateReceipt(Guid commandId, long sequence = 1) => new(
        DeviceCommandValidator.CurrentSchemaVersion,
        Guid.NewGuid(),
        commandId,
        sequence,
        DeviceCommandState.Accepted,
        OccurredAtUtc,
        null);
}

public sealed class DeviceReceiptApiFixture : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Watcher");
    private readonly string _schema = $"receipt_api_test_{Guid.NewGuid():N}";
    private string? _isolatedConnectionString;
    private DeviceReceiptApiWebApplicationFactory? _factory;
    private TestCertificateAuthority? _certificateAuthority;
    private HttpClient? _client;
    private X509Certificate2? _clientCertificate;
    private X509Certificate2? _trustedRoot;
    private Guid _deviceId;
    private Guid _locationId;
    private long _nextSequence;

    public HttpClient Client => _client ?? throw new InvalidOperationException(
        "Для тестів квитанцій потрібен PostgreSQL.");

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

        _certificateAuthority = new TestCertificateAuthority();
        _trustedRoot = _certificateAuthority.Root;
        _clientCertificate = _certificateAuthority.IssueClientCertificate("receipt-test-device");
        _factory = new DeviceReceiptApiWebApplicationFactory(
            _isolatedConnectionString, _clientCertificate, _trustedRoot);
        using IServiceScope scope = _factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        await db.Database.MigrateAsync();
        _locationId = Guid.NewGuid();
        _deviceId = Guid.NewGuid();
        db.Locations.Add(new Location
        {
            Id = _locationId,
            Code = $"r-{_locationId:N}",
            Name = "Тестова локація",
            IsActive = true,
            CreatedAtUtc = OccurredAtUtc
        });
        db.Devices.Add(new Device
        {
            Id = _deviceId,
            LocationId = _locationId,
            Name = "Тестовий вузол",
            CertificateThumbprint = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(_clientCertificate.RawData))
        });
        await db.SaveChangesAsync();
        _client = _factory.CreateTestClient();
    }

    internal async Task<ReceiptCommandSeed> CreateCommandAsync()
    {
        if (_factory is null)
        {
            throw new InvalidOperationException("Для тестів квитанцій потрібен PostgreSQL.");
        }

        var commandId = Guid.NewGuid();
        var sequence = Interlocked.Increment(ref _nextSequence);
        using IServiceScope scope = _factory.Services.CreateScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        db.AlertCommands.Add(new AlertCommand
        {
            Id = commandId,
            IdempotencyKey = Guid.NewGuid(),
            Action = "start",
            PresetCode = "evacuation",
            CreatedAtUtc = OccurredAtUtc,
            ExpiresAtUtc = OccurredAtUtc.AddMinutes(5),
            Targets =
            [
                new CommandTarget
                {
                    Id = Guid.NewGuid(),
                    LocationId = _locationId,
                    Sequence = sequence
                }
            ]
        });
        db.CommandDeliveries.Add(new CommandDelivery
        {
            Id = Guid.NewGuid(),
            CommandId = commandId,
            DeviceId = _deviceId,
            State = CommandDeliveryState.Pending,
            CreatedAtUtc = OccurredAtUtc,
            ExpiresAtUtc = OccurredAtUtc.AddMinutes(5)
        });
        await db.SaveChangesAsync();
        return new ReceiptCommandSeed(commandId, sequence);
    }

    public async Task DisposeAsync()
    {
        _factory?.Dispose();
        _client?.Dispose();
        _clientCertificate?.Dispose();
        _certificateAuthority?.Dispose();
        if (string.IsNullOrWhiteSpace(_connectionString))
        {
            return;
        }

        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand dropSchema = new($"DROP SCHEMA IF EXISTS \"{_schema}\" CASCADE", connection);
        await dropSchema.ExecuteNonQueryAsync();
    }

    private static readonly DateTimeOffset OccurredAtUtc = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
}

internal sealed record ReceiptCommandSeed(Guid CommandId, long Sequence);

internal sealed class DeviceReceiptApiWebApplicationFactory(
    string connectionString,
    X509Certificate2 clientCertificate,
    X509Certificate2 trustedRoot) : WebApplicationFactory<Program>
{
    public HttpClient CreateTestClient() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost")
    });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Watcher"] = connectionString,
                ["Authentication:Google:Enabled"] = "false"
            }));
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter>(new DeviceReceiptClientCertificateStartupFilter(clientCertificate));
            services.PostConfigure<CertificateAuthenticationOptions>("DeviceCertificate", options =>
            {
                options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
                options.RevocationMode = X509RevocationMode.NoCheck;
                options.CustomTrustStore.Add(trustedRoot);
            });
        });
    }

}

internal sealed class DeviceReceiptClientCertificateStartupFilter(X509Certificate2 certificate) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextRequest) =>
        {
            if (context.Request.Headers.ContainsKey("X-Test-Client-Certificate"))
            {
                context.Features.Set<ITlsConnectionFeature>(new DeviceReceiptTlsConnectionFeature(certificate));
            }
            else
            {
                context.Features.Set<ITlsConnectionFeature>(new DeviceReceiptTlsConnectionFeature(null));
            }
            await nextRequest();
        });
        next(app);
    };
}

internal sealed class DeviceReceiptTlsConnectionFeature(X509Certificate2? certificate) : ITlsConnectionFeature
{
    public X509Certificate2? ClientCertificate { get; set; } = certificate;

    public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken) =>
        Task.FromResult(ClientCertificate);
}

