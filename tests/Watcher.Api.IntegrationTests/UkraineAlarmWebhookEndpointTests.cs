using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Watcher.Api.AlertSources;

namespace Watcher.Api.IntegrationTests;

public sealed class UkraineAlarmWebhookEndpointTests
{
    [Fact]
    public async Task Valid_callback_only_signals_reconciliation_and_returns_no_store()
    {
        UkraineAlarmRefreshSignal signal = new();
        using WebApplicationFactory<Program> factory = CreateFactory(signal);
        using HttpClient client = factory.CreateClient();
        using StringContent body = new("""{"status":"active","regionId":"untrusted"}""", Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await client.PostAsync(CallbackPath, body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(signal.Consume());
    }

    [Fact]
    public async Task Callback_does_not_require_undocumented_authentication_headers()
    {
        UkraineAlarmRefreshSignal signal = new();
        using WebApplicationFactory<Program> factory = CreateFactory(signal);
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(CallbackPath, null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.True(signal.Consume());
    }

    [Fact]
    public async Task Production_composition_registers_callback_signal_for_the_endpoint()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(new TestRemoteAddressStartupFilter())));
        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage response = await client.PostAsync(CallbackPath, null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.IsType<UkraineAlarmRefreshSignal>(factory.Services.GetRequiredService<IUkraineAlarmRefreshSignal>());
    }

    [Fact]
    public async Task Callback_over_8_kib_is_rejected_without_signal()
    {
        UkraineAlarmRefreshSignal signal = new();
        using WebApplicationFactory<Program> factory = CreateFactory(signal);
        using HttpClient client = factory.CreateClient();
        using ByteArrayContent body = new(new byte[8 * 1024 + 1]);

        using HttpResponseMessage response = await client.PostAsync(CallbackPath, body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.False(signal.Consume());
    }

    [Fact]
    public async Task Callback_rate_limit_returns_retry_after_after_ten_requests_from_same_address()
    {
        var limitedMetricCount = 0;
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == "ukrainealarm.webhook.requests")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            if (tags.ToArray().Any(tag => tag.Key == "result" && Equals(tag.Value, "rate_limited")))
            {
                Interlocked.Increment(ref limitedMetricCount);
            }
        });
        listener.Start();
        UkraineAlarmRefreshSignal signal = new();
        using WebApplicationFactory<Program> factory = CreateFactory(signal);
        using HttpClient client = factory.CreateClient();

        for (var requestNumber = 0; requestNumber < 10; requestNumber++)
        {
            using HttpResponseMessage accepted = await client.PostAsync(CallbackPath, null);
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        }

        using HttpResponseMessage limited = await client.PostAsync(CallbackPath, null);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Equal("no-store", limited.Headers.CacheControl?.ToString());
        Assert.True(signal.Consume());
        Assert.Equal(1, limitedMetricCount);
    }

    [Fact]
    public void Callback_url_requires_https_origin()
    {
        Uri result = UkraineAlarmWebhookUrl.Create(new Uri("https://alerts.example.test/"));

        Assert.Equal("https://alerts.example.test/api/v1/alert-sources/ukrainealarm/webhook", result.AbsoluteUri);
        Assert.Throws<ArgumentException>(() =>
            UkraineAlarmWebhookUrl.Create(new Uri("http://alerts.example.test/")));
        Assert.Throws<ArgumentException>(() =>
            UkraineAlarmWebhookUrl.Create(new Uri("https://alerts.example.test/app")));
    }

    private static WebApplicationFactory<Program> CreateFactory(
        UkraineAlarmRefreshSignal signal) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IUkraineAlarmRefreshSignal>(signal);
                services.AddSingleton<IStartupFilter>(new TestRemoteAddressStartupFilter());
            }));

    private const string CallbackPath = "/api/v1/alert-sources/ukrainealarm/webhook";

    private sealed class TestRemoteAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => application =>
        {
            application.Use(async (context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                await nextMiddleware();
            });
            next(application);
        };
    }
}
