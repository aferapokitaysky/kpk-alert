using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public sealed class UkraineAlarmApiClient
{
    public const string BaseAddress = "https://api.ukrainealarm.com/";
    private const int MaximumJsonResponseBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly string _token;

    public UkraineAlarmApiClient(HttpClient httpClient, IOptions<UkraineAlarmOptions> options)
    {
        _httpClient = httpClient;
        _token = options.Value.Token ?? string.Empty;
        Uri officialBaseAddress = new(BaseAddress);
        if (httpClient.BaseAddress is null)
        {
            httpClient.BaseAddress = officialBaseAddress;
        }
        else if (httpClient.BaseAddress != officialBaseAddress)
        {
            throw new InvalidOperationException("Клієнт UkraineAlarm має використовувати лише офіційний API origin.");
        }
    }

    public async Task<IReadOnlyList<UkraineAlarmRegion>> GetRegionsAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonAsync(
            HttpMethod.Get, "api/v3/regions", null, cancellationToken);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("states", out JsonElement states) ||
            states.ValueKind != JsonValueKind.Array)
        {
            throw new UkraineAlarmApiException(HttpStatusCode.OK, "invalid_response");
        }

        return ReadRegions(states);
    }

    public async Task<long> GetLastActionIndexAsync(CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonAsync(
            HttpMethod.Get, "api/v3/alerts/status", null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("lastActionIndex", out JsonElement actionIndex) ||
            actionIndex.ValueKind != JsonValueKind.Number ||
            !actionIndex.TryGetInt64(out var value) || value < 0)
        {
            throw new UkraineAlarmApiException(HttpStatusCode.OK, "invalid_response");
        }

        return value;
    }

    public async Task<IReadOnlyList<UkraineAlarmAlertRegion>> GetAlertsAsync(
        CancellationToken cancellationToken) =>
        await ReadAlertRegionsAsync("api/v3/alerts", cancellationToken);

    public async Task<IReadOnlyList<UkraineAlarmAlertRegion>> GetRegionAlertsAsync(
        string regionId,
        CancellationToken cancellationToken) =>
        await ReadAlertRegionsAsync($"api/v3/alerts/{Uri.EscapeDataString(regionId)}", cancellationToken);

    public Task RegisterWebhookAsync(Uri webhookUrl, CancellationToken cancellationToken) =>
        SendEmptyAsync(HttpMethod.Post, "api/v3/webhook", webhookUrl, cancellationToken);

    public Task UpdateWebhookAsync(Uri webhookUrl, CancellationToken cancellationToken) =>
        SendEmptyAsync(HttpMethod.Patch, "api/v3/webhook", webhookUrl, cancellationToken);

    private async Task<IReadOnlyList<UkraineAlarmAlertRegion>> ReadAlertRegionsAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using JsonDocument document = await SendJsonAsync(HttpMethod.Get, path, null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new UkraineAlarmApiException(HttpStatusCode.OK, "invalid_response");
        }

        List<UkraineAlarmAlertRegion> regions = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !TryGetString(item, "regionId", out var regionId) ||
                !TryGetString(item, "regionType", out var regionType) ||
                !TryGetString(item, "regionName", out var regionName) ||
                !TryGetOptionalDate(item, "lastUpdate", out DateTimeOffset? lastUpdate) ||
                !item.TryGetProperty("activeAlerts", out JsonElement activeAlerts) ||
                activeAlerts.ValueKind != JsonValueKind.Array)
            {
                throw new UkraineAlarmApiException(HttpStatusCode.OK, "invalid_response");
            }

            List<UkraineAlarmAlert> alerts = [];
            foreach (JsonElement alert in activeAlerts.EnumerateArray())
            {
                if (alert.ValueKind != JsonValueKind.Object ||
                    !TryGetString(alert, "regionId", out var alertRegionId) ||
                    !TryGetString(alert, "regionType", out var alertRegionType) ||
                    !TryGetString(alert, "type", out var type) ||
                    !TryGetOptionalDate(alert, "lastUpdate", out DateTimeOffset? alertLastUpdate))
                {
                    throw new UkraineAlarmApiException(HttpStatusCode.OK, "invalid_response");
                }

                alerts.Add(new(alertRegionId, alertRegionType, type, alertLastUpdate));
            }

            regions.Add(new(regionId, regionType, regionName, lastUpdate, alerts));
        }

        return regions;
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = CreateRequest(method, path, body);
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        if (response.Content.Headers.ContentLength > MaximumJsonResponseBytes)
        {
            throw new UkraineAlarmApiException(response.StatusCode, "response_too_large");
        }

        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using MemoryStream boundedBody = new();
            var buffer = new byte[8192];
            var totalBytes = 0;
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;
                if (totalBytes > MaximumJsonResponseBytes)
                {
                    throw new UkraineAlarmApiException(response.StatusCode, "response_too_large");
                }

                await boundedBody.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            boundedBody.Position = 0;
            return await JsonDocument.ParseAsync(boundedBody, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new UkraineAlarmApiException(response.StatusCode, "invalid_response");
        }
    }

    private async Task SendEmptyAsync(
        HttpMethod method,
        string path,
        Uri webhookUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(webhookUrl);
        if (!webhookUrl.IsAbsoluteUri || webhookUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("Адреса webhook має використовувати HTTPS.", nameof(webhookUrl));
        }

        using HttpRequestMessage request = CreateRequest(method, path, new UkraineAlarmWebhookRegistration(
            webhookUrl.ToString()));
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        if (string.IsNullOrWhiteSpace(_token))
        {
            throw new InvalidOperationException("Для запитів UkraineAlarm потрібно налаштувати API-токен.");
        }
        if (_token.Any(char.IsControl))
        {
            throw new InvalidOperationException("Токен UkraineAlarm містить недопустимі символи.");
        }

        HttpRequestMessage request = new(method, path);
        request.Headers.TryAddWithoutValidation("Authorization", _token);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryDate)
        {
            TimeSpan untilRetry = retryDate - DateTimeOffset.UtcNow;
            retryAfter = untilRetry > TimeSpan.Zero ? untilRetry : TimeSpan.Zero;
        }

        throw new UkraineAlarmApiException(response.StatusCode, "request_failed", retryAfter);
    }

    private static IReadOnlyList<UkraineAlarmRegion> ReadRegions(JsonElement states)
    {
        List<UkraineAlarmRegion> result = [];
        foreach (JsonElement item in states.EnumerateArray())
        {
            result.Add(ReadRegion(item));
        }

        return result;
    }

    private static UkraineAlarmRegion ReadRegion(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !TryGetString(item, "regionId", out var regionId) ||
            !TryGetString(item, "regionName", out var regionName) ||
            !TryGetString(item, "regionType", out var regionType) ||
            !item.TryGetProperty("regionChildIds", out JsonElement children) ||
            children.ValueKind != JsonValueKind.Array)
        {
            throw new UkraineAlarmApiException(HttpStatusCode.OK, "invalid_response");
        }

        List<UkraineAlarmRegion> childRegions = [];
        foreach (JsonElement child in children.EnumerateArray())
        {
            childRegions.Add(ReadRegion(child));
        }

        return new(regionId, regionName, regionType, childRegions);
    }

    private static bool TryGetString(JsonElement item, string name, out string value)
    {
        value = string.Empty;
        if (!item.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryGetOptionalDate(
        JsonElement item,
        string name,
        out DateTimeOffset? value)
    {
        value = null;
        if (!item.TryGetProperty(name, out JsonElement property) || property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String ||
            !property.TryGetDateTimeOffset(out DateTimeOffset parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private sealed record UkraineAlarmWebhookRegistration(
        [property: JsonPropertyName("webHookUrl")] string WebhookUrl);
}

public sealed record UkraineAlarmRegion(
    string RegionId,
    string RegionName,
    string RegionType,
    IReadOnlyList<UkraineAlarmRegion> Children);

public sealed record UkraineAlarmAlertRegion(
    string RegionId,
    string RegionType,
    string RegionName,
    DateTimeOffset? LastUpdate,
    IReadOnlyList<UkraineAlarmAlert> ActiveAlerts);

public sealed record UkraineAlarmAlert(
    string RegionId,
    string RegionType,
    string Type,
    DateTimeOffset? LastUpdate);

public sealed class UkraineAlarmApiException(
    HttpStatusCode statusCode,
    string errorCode,
    TimeSpan? retryAfter = null) : Exception("Запит до UkraineAlarm API завершився помилкою.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string ErrorCode { get; } = errorCode;
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
