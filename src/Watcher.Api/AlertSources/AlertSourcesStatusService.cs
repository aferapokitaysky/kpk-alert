namespace Watcher.Api.AlertSources;

public sealed class AlertSourcesStatusService(
    IEnumerable<IAlertSourceProvider> providers,
    AlertStatusAggregator aggregator,
    TimeProvider timeProvider,
    ILogger<AlertSourcesStatusService> logger)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(1);
    private readonly IAlertSourceProvider[] _providers = providers.ToArray();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private AlertSourcesStatusResponse? _cachedStatus;

    public async Task<AlertSourcesStatusResponse> GetStatusAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (IsCacheFresh(now))
        {
            return _cachedStatus!;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            now = timeProvider.GetUtcNow();
            if (IsCacheFresh(now))
            {
                return _cachedStatus!;
            }

            Task<IReadOnlyList<AlertSourceObservation>>[] reads = _providers
                .Select(provider => ReadProviderAsync(provider, cancellationToken))
                .ToArray();
            IReadOnlyList<AlertSourceObservation>[] results = await Task.WhenAll(reads);
            now = timeProvider.GetUtcNow();
            _cachedStatus = aggregator.Aggregate(now, results.SelectMany(result => result));
            return _cachedStatus;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsCacheFresh(DateTimeOffset now) =>
        _cachedStatus is not null && now - _cachedStatus.CheckedAtUtc < CacheLifetime;

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
}
