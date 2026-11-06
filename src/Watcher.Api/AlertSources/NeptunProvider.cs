using System.Net;
using System.Text;

namespace Watcher.Api.AlertSources;

public sealed class NeptunProvider(
    IHttpClientFactory httpClientFactory,
    TimeProvider timeProvider,
    ILogger<NeptunProvider> logger) : IAlertSourceProvider
{
    public const string ClientName = "Neptun";
    internal const int MaximumRestResponseBytes = 64 * 1024;

    private readonly object _stateLock = new();
    private NeptunAlertSnapshot? _lastSuccessfulSnapshot;
    private DateTimeOffset _checkedAtUtc;
    private string? _errorCode;
    private bool _realtimeEnabled;

    public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
        DateTimeOffset checkedAtUtc,
        string errorCode)
    {
        SetFailure(errorCode, checkedAtUtc);
        lock (_stateLock)
        {
            return CreateCurrentObservations();
        }
    }

    public async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(
        CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_realtimeEnabled)
            {
                return CreateCurrentObservations();
            }
        }

        await RefreshFromRestAsync(cancellationToken);
        lock (_stateLock)
        {
            return CreateCurrentObservations();
        }
    }

    internal void EnableRealtime()
    {
        lock (_stateLock)
        {
            _realtimeEnabled = true;
            _checkedAtUtc = timeProvider.GetUtcNow();
            _errorCode = "source_unavailable";
        }
    }

    internal void ApplyRealtimeSnapshot(NeptunAlertSnapshot snapshot)
    {
        lock (_stateLock)
        {
            _lastSuccessfulSnapshot = snapshot;
            _checkedAtUtc = timeProvider.GetUtcNow();
            _errorCode = null;
        }
    }

    internal void MarkRealtimeUnavailable(string errorCode)
    {
        lock (_stateLock)
        {
            _checkedAtUtc = timeProvider.GetUtcNow();
            _errorCode = errorCode;
        }
    }

    internal void InvalidateRealtimeSnapshot(string errorCode)
    {
        lock (_stateLock)
        {
            _lastSuccessfulSnapshot = null;
            _checkedAtUtc = timeProvider.GetUtcNow();
            _errorCode = errorCode;
        }
    }

    internal async Task<bool> RefreshFromRestAsync(
        CancellationToken cancellationToken,
        bool allowRealtimeStateUpdate = false)
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
                SetFailure("rate_limited", checkedAt, allowRealtimeStateUpdate);
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Джерело {Source} повернуло HTTP {StatusCode}.",
                    AlertSourceCatalog.NeptunCode, (int)response.StatusCode);
                SetFailure("source_unavailable", checkedAt, allowRealtimeStateUpdate);
                return false;
            }

            byte[] body;
            try
            {
                body = await BoundedHttpContentReader.ReadAsync(
                    response.Content,
                    MaximumRestResponseBytes,
                    cancellationToken);
            }
            catch (BoundedHttpContentReader.ResponseTooLargeException)
            {
                logger.LogWarning("Джерело {Source} перевищило максимальний розмір відповіді.",
                    AlertSourceCatalog.NeptunCode);
                SetFailure("invalid_response", checkedAt, allowRealtimeStateUpdate);
                return false;
            }

            if (!NeptunAlertSnapshotParser.TryParseRest(
                    Encoding.UTF8.GetString(body),
                    out NeptunAlertSnapshot snapshot))
            {
                logger.LogWarning("Джерело {Source} повернуло невідомий формат статусу.",
                    AlertSourceCatalog.NeptunCode);
                SetFailure("invalid_response", checkedAt, allowRealtimeStateUpdate);
                return false;
            }

            lock (_stateLock)
            {
                if (allowRealtimeStateUpdate || !_realtimeEnabled)
                {
                    _lastSuccessfulSnapshot = snapshot;
                    _checkedAtUtc = checkedAt;
                    _errorCode = null;
                }
            }
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Не вдалося отримати відповідь від джерела {Source}.",
                AlertSourceCatalog.NeptunCode);
            SetFailure("source_unavailable", timeProvider.GetUtcNow(), allowRealtimeStateUpdate);
            return false;
        }
        catch (IOException)
        {
            logger.LogWarning("Не вдалося прочитати відповідь від джерела {Source}.",
                AlertSourceCatalog.NeptunCode);
            SetFailure("source_unavailable", timeProvider.GetUtcNow(), allowRealtimeStateUpdate);
            return false;
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Вичерпано час очікування відповіді від джерела {Source}.",
                AlertSourceCatalog.NeptunCode);
            SetFailure("source_unavailable", timeProvider.GetUtcNow(), allowRealtimeStateUpdate);
            return false;
        }
    }

    private void SetFailure(string errorCode, DateTimeOffset checkedAtUtc, bool allowRealtimeStateUpdate = false)
    {
        lock (_stateLock)
        {
            if (_realtimeEnabled && !allowRealtimeStateUpdate)
            {
                return;
            }

            _checkedAtUtc = checkedAtUtc;
            _errorCode = errorCode;
        }
    }

    private IReadOnlyList<AlertSourceObservation> CreateCurrentObservations()
    {
        NeptunAlertSnapshot? snapshot = _lastSuccessfulSnapshot;
        var fresh = _errorCode is null && snapshot is not null;
        return [
            CreateObservation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
                AlertSourceCatalog.PoltavaOblastName, snapshot?.OblastState,
                fresh ? AlertFreshness.Fresh : snapshot is null ? AlertFreshness.Unknown : AlertFreshness.Stale,
                _checkedAtUtc, _errorCode),
            CreateObservation(AlertScope.Raion, AlertSourceCatalog.KremenchukRaionId,
                AlertSourceCatalog.KremenchukRaionName, snapshot?.RaionState,
                fresh ? AlertFreshness.Fresh : snapshot is null ? AlertFreshness.Unknown : AlertFreshness.Stale,
                _checkedAtUtc, _errorCode)
        ];
    }

    private static AlertSourceObservation CreateObservation(
        AlertScope scope,
        string scopeId,
        string scopeName,
        AlertState? state,
        AlertFreshness freshness,
        DateTimeOffset checkedAtUtc,
        string? errorCode) =>
        new(
            AlertSourceCatalog.NeptunCode,
            AlertSourceCatalog.NeptunName,
            scope,
            scopeId,
            scopeName,
            freshness == AlertFreshness.Fresh && state is not null ? state.Value : AlertState.Unknown,
            null,
            freshness,
            checkedAtUtc,
            null,
            errorCode,
            AlertSourceCatalog.NeptunAttributionUrl,
            freshness == AlertFreshness.Stale ? state : null);

}
