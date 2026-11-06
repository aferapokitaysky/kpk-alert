using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Watcher.Api.Controllers;

namespace Watcher.UnitTests;

public sealed class ApiHealthReadinessFailureTests
{
    [Fact]
    public async Task Ready_returns_sanitized_unavailable_when_database_scope_creation_fails()
    {
        const string sensitiveDetail = "internal database password detail";
        IConfiguration configuration = CreateConfiguration();
        var scopeFactory = new ThrowingScopeFactory(new InvalidOperationException(sensitiveDetail));
        var logger = new CapturingLogger<HealthController>();
        var controller = new HealthController(configuration, scopeFactory, logger);

        ObjectResult result = Assert.IsType<ObjectResult>(
            await controller.Ready(CancellationToken.None));
        var responseBody = JsonSerializer.Serialize(result.Value);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.Contains("database", responseBody, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, responseBody, StringComparison.Ordinal);
        Assert.Equal(1, scopeFactory.CreatedScopeCount);
        var logMessage = Assert.Single(logger.Messages);
        Assert.Contains(nameof(InvalidOperationException), logMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveDetail, logMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ready_rethrows_scope_creation_cancellation_when_request_token_is_cancelled()
    {
        IConfiguration configuration = CreateConfiguration();
        var scopeFactory = new ThrowingScopeFactory(new OperationCanceledException("request cancelled"));
        var logger = new CapturingLogger<HealthController>();
        var controller = new HealthController(configuration, scopeFactory, logger);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => controller.Ready(cancellation.Token));

        Assert.Equal(1, scopeFactory.CreatedScopeCount);
        Assert.Empty(logger.Messages);
    }

    private static IConfiguration CreateConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Watcher"] = "Host=database;Password=do-not-disclose"
        })
        .Build();

    private sealed class ThrowingScopeFactory(Exception exception) : IServiceScopeFactory
    {
        public int CreatedScopeCount { get; private set; }

        public IServiceScope CreateScope()
        {
            CreatedScopeCount++;
            throw exception;
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IReadOnlyCollection<string> Messages => _messages.ToArray();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            _messages.Enqueue(formatter(state, exception));
            if (exception is not null)
            {
                _messages.Enqueue(exception.ToString());
            }
        }
    }
}
