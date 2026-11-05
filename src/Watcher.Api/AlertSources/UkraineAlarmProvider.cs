using Microsoft.Extensions.Options;

namespace Watcher.Api.AlertSources;

public sealed class UkraineAlarmProvider(
    UkraineAlarmApiClient client,
    IOptions<UkraineAlarmOptions> options,
    IUkraineAlarmRefreshSignal refreshSignal,
    TimeProvider timeProvider,
    ILogger<UkraineAlarmProvider> logger) : IAlertSourceProvider
{
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(1);
    private readonly SemaphoreSlim _readLock = new(1, 1);
    private long? _lastActionIndex;
    private DateTimeOffset _nextAttemptAtUtc;
    private int _consecutiveFailures;
    private bool _awaitingActionIndexAdvance;
    private string? _lastBackoffErrorCode;
    private IReadOnlyList<AlertSourceObservation>? _lastObservations;

    public async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken)
    {
        await _readLock.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(cancellationToken);
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task<IReadOnlyList<AlertSourceObservation>> ReadCoreAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        var callbackReceived = refreshSignal.Consume();
        if (string.IsNullOrWhiteSpace(options.Value.Token))
        {
            return CreateUnavailableObservations(now, "not_configured");
        }

        if (now < _nextAttemptAtUtc)
        {
            if (callbackReceived)
            {
                _awaitingActionIndexAdvance = true;
            }

            if (_awaitingActionIndexAdvance || _lastObservations is null)
            {
                return CreateUnavailableObservations(now, _lastBackoffErrorCode ?? "retry_backoff");
            }

            return _lastBackoffErrorCode is null
                ? _lastObservations
                : CreateUnavailableObservations(now, _lastBackoffErrorCode);
        }

        try
        {
            var actionIndex = await client.GetLastActionIndexAsync(cancellationToken);
            if (_lastActionIndex is long lastIndex && actionIndex < lastIndex)
            {
                _awaitingActionIndexAdvance = true;
                ScheduleRetry(now, null, "action_index_rollback");
                return CreateUnavailableObservations(now, "action_index_rollback");
            }

            if (_awaitingActionIndexAdvance && _lastActionIndex is long confirmedIndex &&
                actionIndex <= confirmedIndex)
            {
                ScheduleRetry(now, null, "action_index_not_advanced");
                return CreateUnavailableObservations(now, "action_index_not_advanced");
            }

            var needsSnapshot = _lastObservations is null ||
                _lastActionIndex != actionIndex || callbackReceived || _awaitingActionIndexAdvance;
            if (callbackReceived && _lastActionIndex is long previousIndex && actionIndex <= previousIndex)
            {
                // Неописане тіло callback не може змінювати стан без нового індексу API.
                _awaitingActionIndexAdvance = true;
                ScheduleRetry(now, null, "action_index_not_advanced");
                return CreateUnavailableObservations(now, "action_index_not_advanced");
            }

            if (needsSnapshot)
            {
                IReadOnlyList<AlertSourceObservation> snapshot = await ReadSnapshotAsync(now, cancellationToken);
                var confirmedActionIndex = await client.GetLastActionIndexAsync(cancellationToken);
                if (confirmedActionIndex != actionIndex)
                {
                    ScheduleRetry(now, null, "snapshot_changed_during_read");
                    return CreateUnavailableObservations(now, "snapshot_changed_during_read");
                }

                _lastObservations = snapshot;
                _lastActionIndex = actionIndex;
                _awaitingActionIndexAdvance = false;
            }
            else
            {
                _lastObservations = _lastObservations!
                    .Select(observation => observation with
                    {
                        Freshness = AlertFreshness.Fresh,
                        CheckedAtUtc = now,
                        ErrorCode = null
                    })
                    .ToArray();
            }

            _consecutiveFailures = 0;
            _lastBackoffErrorCode = null;
            _nextAttemptAtUtc = now + UkraineAlarmOptions.MinimumPollInterval;
            return _lastObservations;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UkraineAlarmApiException exception)
        {
            var errorCode = exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                ? "rate_limited"
                : exception.ErrorCode;
            logger.LogWarning(
                "UkraineAlarm недоступний: {ErrorCode}, HTTP {StatusCode}.",
                errorCode,
                (int)exception.StatusCode);
            ScheduleRetry(now, exception.RetryAfter, errorCode);
            return CreateUnavailableObservations(now, errorCode);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Не вдалося отримати відповідь від UkraineAlarm.");
            ScheduleRetry(now, null, "source_unavailable");
            return CreateUnavailableObservations(now, "source_unavailable");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Вичерпано час очікування відповіді від UkraineAlarm.");
            ScheduleRetry(now, null, "source_unavailable");
            return CreateUnavailableObservations(now, "source_unavailable");
        }
    }

    public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
        DateTimeOffset checkedAtUtc,
        string errorCode)
    {
        IReadOnlyList<AlertSourceObservation> observations = _lastObservations is null
            ? CreateUnknownObservations(checkedAtUtc, errorCode)
            : _lastObservations.Select(observation => observation with
            {
                State = AlertState.Unknown,
                Freshness = AlertFreshness.Stale,
                CheckedAtUtc = checkedAtUtc,
                ErrorCode = errorCode,
                LastKnownState = observation.State,
                LastKnownAlertLevel = observation.AlertLevel
            }).ToArray();

        return observations;
    }

    private async Task<IReadOnlyList<AlertSourceObservation>> ReadSnapshotAsync(
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UkraineAlarmRegion> regionTree = await client.GetRegionsAsync(cancellationToken);
        UkraineAlarmOptions settings = options.Value;
        UkraineAlarmRegion? oblast = FindUniqueRegion(regionTree, settings.PoltavaOblastName, "State");
        UkraineAlarmRegion? hromada = FindUniqueRegion(regionTree, settings.KremenchukHromadaName, "Community");

        AlertSourceObservation oblastObservation = await ReadTargetAsync(
            oblast,
            AlertScope.Oblast,
            AlertSourceCatalog.PoltavaOblastId,
            AlertSourceCatalog.PoltavaOblastName,
            checkedAtUtc,
            cancellationToken);
        AlertSourceObservation hromadaObservation = await ReadTargetAsync(
            hromada,
            AlertScope.Hromada,
            AlertSourceCatalog.KremenchukHromadaId,
            AlertSourceCatalog.KremenchukHromadaName,
            checkedAtUtc,
            cancellationToken);

        return [oblastObservation, hromadaObservation];
    }

    private async Task<AlertSourceObservation> ReadTargetAsync(
        UkraineAlarmRegion? region,
        AlertScope scope,
        string canonicalScopeId,
        string scopeName,
        DateTimeOffset checkedAtUtc,
        CancellationToken cancellationToken)
    {
        if (region is null)
        {
            return CreateObservation(scope, canonicalScopeId, scopeName, AlertState.Unknown,
                AlertFreshness.Unknown, checkedAtUtc, null, "region_not_found");
        }

        IReadOnlyList<UkraineAlarmAlertRegion> response = await client.GetRegionAlertsAsync(
            region.RegionId, cancellationToken);
        if (response.Count == 0)
        {
            return CreateObservation(scope, canonicalScopeId, scopeName, AlertState.Inactive,
                AlertFreshness.Fresh, checkedAtUtc, null, null);
        }

        UkraineAlarmAlertRegion[] matching = response.Where(item =>
            string.Equals(item.RegionId, region.RegionId, StringComparison.Ordinal) &&
            string.Equals(item.RegionType, region.RegionType, StringComparison.Ordinal)).ToArray();
        if (matching.Length != 1 || response.Count != 1)
        {
            return CreateObservation(scope, canonicalScopeId, scopeName, AlertState.Unknown,
                AlertFreshness.Unknown, checkedAtUtc, null, "invalid_response");
        }

        UkraineAlarmAlertRegion current = matching[0];
        DateTimeOffset? dataUpdatedAt = current.ActiveAlerts
            .Select(alert => alert.LastUpdate)
            .Append(current.LastUpdate)
            .Max();
        AlertState state = current.ActiveAlerts.Count == 0 ? AlertState.Inactive : AlertState.Active;
        return CreateObservation(scope, canonicalScopeId, scopeName, state,
            AlertFreshness.Fresh, checkedAtUtc, dataUpdatedAt, null);
    }

    private static UkraineAlarmRegion? FindUniqueRegion(
        IReadOnlyList<UkraineAlarmRegion> roots,
        string expectedName,
        string expectedType)
    {
        List<UkraineAlarmRegion> matches = [];
        Stack<UkraineAlarmRegion> pending = new(roots);
        while (pending.TryPop(out UkraineAlarmRegion? current))
        {
            if (string.Equals(current.RegionName.Trim(), expectedName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.RegionType, expectedType, StringComparison.Ordinal))
            {
                matches.Add(current);
            }

            foreach (UkraineAlarmRegion child in current.Children)
            {
                pending.Push(child);
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    private void ScheduleRetry(DateTimeOffset now, TimeSpan? providerDelay, string errorCode)
    {
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, 6);
        var multiplier = Math.Pow(2, _consecutiveFailures - 1);
        var exponentialDelay = TimeSpan.FromTicks((long)(InitialRetryDelay.Ticks * multiplier));
        TimeSpan delay = providerDelay ?? exponentialDelay;
        if (providerDelay is null && delay > MaximumRetryDelay)
        {
            delay = MaximumRetryDelay;
        }

        if (delay < UkraineAlarmOptions.MinimumPollInterval)
        {
            delay = UkraineAlarmOptions.MinimumPollInterval;
        }

        _nextAttemptAtUtc = now + delay;
        _lastBackoffErrorCode = errorCode;
    }

    private static IReadOnlyList<AlertSourceObservation> CreateUnknownObservations(
        DateTimeOffset checkedAtUtc,
        string errorCode) =>
    [
        CreateObservation(AlertScope.Oblast, AlertSourceCatalog.PoltavaOblastId,
            AlertSourceCatalog.PoltavaOblastName, AlertState.Unknown, AlertFreshness.Unknown,
            checkedAtUtc, null, errorCode),
        CreateObservation(AlertScope.Hromada, AlertSourceCatalog.KremenchukHromadaId,
            AlertSourceCatalog.KremenchukHromadaName, AlertState.Unknown, AlertFreshness.Unknown,
            checkedAtUtc, null, errorCode)
    ];

    private static AlertSourceObservation CreateObservation(
        AlertScope scope,
        string scopeId,
        string scopeName,
        AlertState state,
        AlertFreshness freshness,
        DateTimeOffset checkedAtUtc,
        DateTimeOffset? dataUpdatedAtUtc,
        string? errorCode,
        string? alertLevel = null) =>
        new(
            AlertSourceCatalog.UkraineAlarmCode,
            AlertSourceCatalog.UkraineAlarmName,
            scope,
            scopeId,
            scopeName,
            state,
            alertLevel,
            freshness,
            checkedAtUtc,
            dataUpdatedAtUtc,
            errorCode,
            AlertSourceCatalog.UkraineAlarmAttributionUrl);
}
