namespace Watcher.Api.AlertSources;

public sealed class AlertSourcesStatusService(
    IEnumerable<IAlertSourceProvider> providers,
    AlertStatusAggregator aggregator,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<AlertSourcesStatusService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private readonly IAlertSourceProvider[] _providers = providers.ToArray();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedStatus? _cachedStatus;
    private long _cacheVersion;

    public async Task<AlertSourcesStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset now = timeProvider.GetUtcNow();
            var version = Volatile.Read(ref _cacheVersion);
            if (IsCacheFresh(now, version))
            {
                return Volatile.Read(ref _cachedStatus)!.Status;
            }

            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                now = timeProvider.GetUtcNow();
                version = Volatile.Read(ref _cacheVersion);
                if (IsCacheFresh(now, version))
                {
                    return Volatile.Read(ref _cachedStatus)!.Status;
                }

                Task<IReadOnlyList<AlertSourceObservation>>[] reads = _providers
                    .Select(provider => ReadProviderAsync(provider, cancellationToken))
                    .ToArray();
                IReadOnlyList<AlertSourceObservation>[] results = await Task.WhenAll(reads);
                now = timeProvider.GetUtcNow();
                AlertSourcesStatusResponse status = aggregator.Aggregate(now, results.SelectMany(result => result));
                if (version == Volatile.Read(ref _cacheVersion))
                {
                    await RecordObservationChangesAsync(
                        results.SelectMany(result => result), cancellationToken);
                    Volatile.Write(ref _cachedStatus, new CachedStatus(version, status));
                    return status;
                }
            }
            finally
            {
                _refreshLock.Release();
            }
        }
    }

    public void InvalidateCache() => Interlocked.Increment(ref _cacheVersion);

    private bool IsCacheFresh(DateTimeOffset now, long version)
    {
        CachedStatus? cached = Volatile.Read(ref _cachedStatus);
        return cached is not null && cached.Version == version &&
               now - cached.Status.CheckedAtUtc < CacheLifetime;
    }

    private sealed record CachedStatus(long Version, AlertSourcesStatusResponse Status);

    private async Task<IReadOnlyList<AlertSourceObservation>> ReadProviderAsync(
        IAlertSourceProvider provider,
        CancellationToken cancellationToken)
    {
        try
        {
            return await provider.ReadAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Помилка провайдера статусу {ProviderType} ({ExceptionType}).",
                provider.GetType().Name,
                exception.GetType().Name);
            return provider.CreateUnavailableObservations(
                timeProvider.GetUtcNow(),
                "source_unavailable");
        }
    }

    private async Task RecordObservationChangesAsync(
        IEnumerable<AlertSourceObservation> observations,
        CancellationToken cancellationToken)
    {
        try
        {
            await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
            AlertSourceObservationAuditWriter? writer = scope.ServiceProvider
                .GetService<AlertSourceObservationAuditWriter>();
            if (writer is not null)
            {
                await writer.RecordChangesAsync(observations, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                "Не вдалося зберегти зміну стану джерела тривоги ({ExceptionType}).",
                exception.GetType().Name);
        }
    }
}
