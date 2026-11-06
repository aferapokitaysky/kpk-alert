using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Api.Controllers;

namespace Watcher.UnitTests;

public sealed class ApiHealthReadinessTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Ready_returns_sanitized_database_unavailable_when_connection_is_not_configured(
        string? connectionString)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Watcher"] = connectionString
            })
            .Build();
        var scopeFactory = new TrackingScopeFactory();
        var controller = new HealthController(
            configuration,
            scopeFactory,
            NullLogger<HealthController>.Instance);

        ObjectResult result = Assert.IsType<ObjectResult>(
            await controller.Ready(CancellationToken.None));

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        ReadinessResponse? response = JsonSerializer.Deserialize<ReadinessResponse>(
            JsonSerializer.Serialize(result.Value),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(response);
        Assert.Equal("unavailable", response.Status);
        Assert.Equal("database", response.Dependency);
        Assert.Equal(0, scopeFactory.CreatedScopeCount);
    }

    private sealed class TrackingScopeFactory : IServiceScopeFactory
    {
        public int CreatedScopeCount { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopeCount++;
            throw new InvalidOperationException("A database scope must not be created without configuration.");
        }
    }

    private sealed record ReadinessResponse(string Status, string Dependency);
}
