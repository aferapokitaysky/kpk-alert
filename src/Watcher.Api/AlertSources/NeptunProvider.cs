using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;

namespace Watcher.Api.AlertSources;

public sealed class NeptunProvider(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<NeptunProvider> logger) : IAlertSourceProvider
{
    public const string ClientName = "Neptun";

    private readonly ConcurrentDictionary<string, CachedStatus> _lastSuccessfulStatuses = new();

    public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
        DateTimeOffset checkedAtUtc,
        string errorCode) => Fail(errorCode, checkedAtUtc);

    public async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpClient client = httpClientFactory.CreateClient(ClientName);
            using HttpResponseMessage response = await client.GetAsync(
                "api/v1/alerts",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            DateTimeOffset checkedAt = timeProvider.GetUtcNow();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Fail("rate_limited", checkedAt);
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Джерело {Source} повернуло HTTP {StatusCode}.",
                    AlertSourceCatalog.NeptunCode, (int)response.StatusCode);
                return Fail("source_unavailable", checkedAt);
            }

            await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using JsonDocument document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            if (!TryReadStatus(
                    document.RootElement,
                    out var oblastActive,
                    out var raionActive))
            {
                logger.LogWarning("Джерело {Source} повернуло невідомий формат статусу.",
                    AlertSourceCatalog.NeptunCode);
                return Fail("invalid_response", checkedAt);
            }

            _lastSuccessfulStatuses["oblast"] = new(
                oblastActive ? AlertState.Active : AlertState.Inactive);
            _lastSuccessfulStatuses["raion"] = new(
                raionActive ? AlertState.Active : AlertState.Inactive);
            return [
                CreateObservation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                    AlertSourceCatalog.PoltavaOblastName,
                    _lastSuccessfulStatuses["oblast"], AlertFreshness.Fresh, checkedAt, null),
                CreateObservation(AlertScope.Raion, AlertSourceCatalog.KremenchukRaionId,
                    AlertSourceCatalog.KremenchukRaionName, _lastSuccessfulStatuses["raion"],
                    AlertFreshness.Fresh, checkedAt, null)
            ];
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Не вдалося отримати відповідь від джерела {Source}.",
                AlertSourceCatalog.NeptunCode);
            return Fail("source_unavailable", timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Вичерпано час очікування відповіді від джерела {Source}.",
                AlertSourceCatalog.NeptunCode);
            return Fail("source_unavailable", timeProvider.GetUtcNow());
        }
        catch (JsonException)
        {
            logger.LogWarning("Джерело {Source} повернуло некоректний JSON.",
                AlertSourceCatalog.NeptunCode);
            return Fail("invalid_response", timeProvider.GetUtcNow());
        }
    }

    private static bool TryReadStatus(
        JsonElement root,
        out bool oblastActive,
        out bool raionActive)
    {
        oblastActive = false;
        raionActive = false;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("oblasts", out JsonElement oblasts) || oblasts.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("raions", out JsonElement raions) || raions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        if (!TryReadLocations(oblasts, out IReadOnlyList<NeptunLocation> oblastEntries) ||
            !TryReadLocations(raions, out IReadOnlyList<NeptunLocation> raionEntries))
        {
            return false;
        }

        NeptunLocation? activeOblast = oblastEntries.FirstOrDefault(location =>
            string.Equals(location.Key, AlertSourceCatalog.PoltavaOblastNeptunKey, StringComparison.Ordinal));
        NeptunLocation? activeRaion = raionEntries.FirstOrDefault(location =>
            string.Equals(location.Key, AlertSourceCatalog.KremenchukRaionId, StringComparison.Ordinal));
        var oblastNameWithoutKnownKey = activeOblast is null && oblastEntries.Any(location =>
            string.Equals(location.Name, AlertSourceCatalog.PoltavaOblastName, StringComparison.Ordinal));
        var raionNameWithoutKnownKey = activeRaion is null && raionEntries.Any(location =>
            string.Equals(location.Name, AlertSourceCatalog.KremenchukRaionName, StringComparison.Ordinal) &&
            string.Equals(location.Oblast, AlertSourceCatalog.PoltavaOblastName, StringComparison.Ordinal));
        if (oblastNameWithoutKnownKey || raionNameWithoutKnownKey)
        {
            return false;
        }

        oblastActive = activeOblast is not null;
        raionActive = activeRaion is not null;
        return true;
    }

    private static bool TryReadLocations(JsonElement array, out IReadOnlyList<NeptunLocation> locations)
    {
        List<NeptunLocation> found = [];
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("key", out JsonElement key) || key.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("name", out JsonElement name) || name.ValueKind != JsonValueKind.String ||
                !item.TryGetProperty("oblast", out JsonElement oblast) || oblast.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) || string.IsNullOrWhiteSpace(name.GetString()) ||
                !keys.Add(key.GetString()!))
            {
                locations = [];
                return false;
            }

            found.Add(new(key.GetString()!, name.GetString()!, oblast.GetString()));
        }

        locations = found;
        return true;
    }

    private IReadOnlyList<AlertSourceObservation> Fail(string errorCode, DateTimeOffset checkedAtUtc)
    {
        _lastSuccessfulStatuses.TryGetValue("oblast", out CachedStatus? oblast);
        _lastSuccessfulStatuses.TryGetValue("raion", out CachedStatus? raion);
        return [
            CreateObservation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.PoltavaOblastName,
                oblast, AlertFreshness.Stale, checkedAtUtc, errorCode),
            CreateObservation(AlertScope.Raion, AlertSourceCatalog.KremenchukRaionId,
                AlertSourceCatalog.KremenchukRaionName, raion, AlertFreshness.Stale, checkedAtUtc, errorCode)
        ];
    }

    private static AlertSourceObservation CreateObservation(
        AlertScope scope,
        string scopeId,
        string scopeName,
        CachedStatus? status,
        AlertFreshness freshness,
        DateTimeOffset checkedAtUtc,
        string? errorCode) =>
        new(
            AlertSourceCatalog.NeptunCode,
            AlertSourceCatalog.NeptunName,
            scope,
            scopeId,
            scopeName,
            freshness == AlertFreshness.Fresh && status is not null ? status.State : AlertState.Unknown,
            null,
            status is null && freshness != AlertFreshness.Fresh ? AlertFreshness.Unknown : freshness,
            checkedAtUtc,
            null,
            errorCode,
            AlertSourceCatalog.NeptunAttributionUrl,
            freshness == AlertFreshness.Stale ? status?.State : null);

    private sealed record CachedStatus(AlertState State);

    private sealed record NeptunLocation(string Key, string Name, string? Oblast);
}
