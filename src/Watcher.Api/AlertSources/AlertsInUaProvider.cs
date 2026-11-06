using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public sealed class AlertsInUaProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<AlertsInUaOptions> options,
    TimeProvider timeProvider,
    ILogger<AlertsInUaProvider> logger) : IAlertSourceProvider
{
    public const string ClientName = "AlertsInUa";
    private const int MaximumResponseBytes = 64 * 1024;

    private readonly ConcurrentDictionary<string, CachedStatus> _lastSuccessfulStatuses = new();

    public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
        DateTimeOffset checkedAtUtc,
        string errorCode)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Token))
        {
            return [
                MissingToken(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                    AlertSourceCatalog.PoltavaOblastName),
                MissingToken(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
                    AlertSourceCatalog.KremenchukHromadaName)
            ];
        }

        _lastSuccessfulStatuses.TryGetValue(AlertSourceCatalog.PoltavaOblastId, out CachedStatus? oblast);
        _lastSuccessfulStatuses.TryGetValue(AlertSourceCatalog.KremenchukHromadaId, out CachedStatus? hromada);
        return [
            Failure(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.PoltavaOblastName, errorCode, oblast, checkedAtUtc),
            Failure(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
                AlertSourceCatalog.KremenchukHromadaName, errorCode, hromada, checkedAtUtc)
        ];
    }

    public async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Token))
        {
            return [
                MissingToken(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                    AlertSourceCatalog.PoltavaOblastName),
                MissingToken(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
                    AlertSourceCatalog.KremenchukHromadaName)
            ];
        }

        Task<AlertSourceObservation> oblast = ReadScopeAsync(
            AlertScope.Oblast,
            AlertSourceCatalog.PoltavaOblastId,
            AlertSourceCatalog.PoltavaOblastName,
            cancellationToken);
        Task<AlertSourceObservation> hromada = ReadScopeAsync(
            AlertScope.Hromada,
            AlertSourceCatalog.KremenchukHromadaId,
            AlertSourceCatalog.KremenchukHromadaName,
            cancellationToken);

        return await Task.WhenAll(oblast, hromada);
    }

    private async Task<AlertSourceObservation> ReadScopeAsync(
        AlertScope scope,
        string scopeId,
        string scopeName,
        CancellationToken cancellationToken)
    {
        _lastSuccessfulStatuses.TryGetValue(scopeId, out CachedStatus? previous);
        using HttpRequestMessage request = new(
            HttpMethod.Get,
            $"v2/iot/active_air_raid_alerts/{scopeId}.json");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Value.Token);
        if (previous?.LastModified is not null)
        {
            request.Headers.IfModifiedSince = previous.LastModified;
        }

        try
        {
            using HttpClient client = httpClientFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            DateTimeOffset checkedAt = timeProvider.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
            {
                CachedStatus refreshed = previous with { CheckedAtUtc = checkedAt };
                _lastSuccessfulStatuses[scopeId] = refreshed;
                return ToObservation(scope, scopeId, scopeName, refreshed);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Failure(scope, scopeId, scopeName, "rate_limited", previous, checkedAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Джерело {Source} повернуло HTTP {StatusCode}.",
                    AlertSourceCatalog.AlertsInUaCode, (int)response.StatusCode);
                return Failure(scope, scopeId, scopeName, "source_unavailable", previous, checkedAt);
            }

            var body = await BoundedHttpContentReader.ReadAsync(
                response.Content,
                MaximumResponseBytes,
                cancellationToken);
            if (!TryParseStatus(body, out AlertState state, out var alertLevel))
            {
                logger.LogWarning("Джерело {Source} повернуло невідомий формат статусу.",
                    AlertSourceCatalog.AlertsInUaCode);
                return Failure(scope, scopeId, scopeName, "invalid_response", previous, checkedAt);
            }

            DateTimeOffset? lastModified = response.Content.Headers.LastModified;
            if (lastModified is null && response.Headers.TryGetValues("Last-Modified", out IEnumerable<string>? values) &&
                DateTimeOffset.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedLastModified))
            {
                lastModified = parsedLastModified;
            }
            CachedStatus current = new(
                state,
                alertLevel,
                lastModified,
                checkedAt,
                lastModified);
            _lastSuccessfulStatuses[scopeId] = current;
            return ToObservation(scope, scopeId, scopeName, current);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Не вдалося отримати відповідь від джерела {Source}.",
                AlertSourceCatalog.AlertsInUaCode);
            return Failure(scope, scopeId, scopeName, "source_unavailable", previous,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Вичерпано час очікування відповіді від джерела {Source}.",
                AlertSourceCatalog.AlertsInUaCode);
            return Failure(scope, scopeId, scopeName, "source_unavailable", previous,
                timeProvider.GetUtcNow());
        }
        catch (JsonException)
        {
            logger.LogWarning("Джерело {Source} повернуло некоректний JSON.",
                AlertSourceCatalog.AlertsInUaCode);
            return Failure(scope, scopeId, scopeName, "invalid_response", previous,
                timeProvider.GetUtcNow());
        }
        catch (BoundedHttpContentReader.ResponseTooLargeException)
        {
            logger.LogWarning("Джерело {Source} перевищило допустимий розмір відповіді.",
                AlertSourceCatalog.AlertsInUaCode);
            return Failure(scope, scopeId, scopeName, "invalid_response", previous,
                timeProvider.GetUtcNow());
        }
    }

    private static bool TryParseStatus(ReadOnlyMemory<byte> body, out AlertState state, out string? alertLevel)
    {
        state = AlertState.Unknown;
        alertLevel = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            var statusCode = document.RootElement.GetString();
            state = statusCode switch
            {
                "R" or "Y" => AlertState.Active,
                "N" => AlertState.Unknown,
                "P" => AlertState.Partial,
                " " => AlertState.Unknown,
                _ => AlertState.Unknown
            };
            alertLevel = statusCode switch
            {
                "R" => "red",
                "Y" => "yellow",
                _ => null
            };
            return statusCode is "R" or "Y" or "N" or "P" or " ";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static AlertSourceObservation MissingToken(AlertScope scope, string scopeId, string scopeName) =>
        new(
            AlertSourceCatalog.AlertsInUaCode,
            AlertSourceCatalog.AlertsInUaName,
            scope,
            scopeId,
            scopeName,
            AlertState.Unknown,
            null,
            AlertFreshness.Unknown,
            null,
            null,
            "not_configured",
            AlertSourceCatalog.AlertsInUaAttributionUrl);

    private static AlertSourceObservation Failure(
        AlertScope scope,
        string scopeId,
        string scopeName,
        string errorCode,
        CachedStatus? previous,
        DateTimeOffset checkedAtUtc) =>
        new(
            AlertSourceCatalog.AlertsInUaCode,
            AlertSourceCatalog.AlertsInUaName,
            scope,
            scopeId,
            scopeName,
            AlertState.Unknown,
            null,
            previous is null ? AlertFreshness.Unknown : AlertFreshness.Stale,
            checkedAtUtc,
            previous?.DataUpdatedAtUtc,
            errorCode,
            AlertSourceCatalog.AlertsInUaAttributionUrl,
            previous?.State,
            previous?.AlertLevel);

    private static AlertSourceObservation ToObservation(
        AlertScope scope,
        string scopeId,
        string scopeName,
        CachedStatus status) =>
        new(
            AlertSourceCatalog.AlertsInUaCode,
            AlertSourceCatalog.AlertsInUaName,
            scope,
            scopeId,
            scopeName,
            status.State,
            status.AlertLevel,
            AlertFreshness.Fresh,
            status.CheckedAtUtc,
            status.DataUpdatedAtUtc,
            null,
            AlertSourceCatalog.AlertsInUaAttributionUrl);

    private sealed record CachedStatus(
        AlertState State,
        string? AlertLevel,
        DateTimeOffset? LastModified,
        DateTimeOffset CheckedAtUtc,
        DateTimeOffset? DataUpdatedAtUtc);
}
