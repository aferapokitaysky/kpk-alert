using Microsoft.Extensions.Logging.Abstractions;
using Watcher.Api.AlertSources;

namespace Watcher.UnitTests.AlertSources;

public sealed class AlertSourcesStatusCacheInvalidationTests
{
    [Fact]
    public async Task Invalidate_cache_causes_a_new_provider_read()
    {
        CountingProvider provider = new();
        AlertSourcesStatusService service = CreateService(provider);

        await service.GetStatusAsync(CancellationToken.None);
        await service.GetStatusAsync(CancellationToken.None);
        service.InvalidateCache();
        await service.GetStatusAsync(CancellationToken.None);

        Assert.Equal(2, provider.ReadCount);
    }

    [Fact]
    public async Task Invalidation_during_provider_read_discards_the_stale_snapshot()
    {
        BlockingProvider provider = new();
        AlertSourcesStatusService service = CreateService(provider);

        Task<AlertSourcesStatusResponse> pendingRead = service.GetStatusAsync(CancellationToken.None);
        await provider.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        service.InvalidateCache();
        provider.ReleaseFirstRead.TrySetResult();
        await pendingRead;

        Assert.Equal(2, provider.ReadCount);
    }

    private static AlertSourcesStatusService CreateService(IAlertSourceProvider provider) => new(
        [provider],
        new AlertStatusAggregator(),
        new FixedTimeProvider(new DateTimeOffset(2026, 11, 6, 8, 0, 0, TimeSpan.Zero)),
        NullLogger<AlertSourcesStatusService>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private class CountingProvider : IAlertSourceProvider
    {
        private int _readCount;

        public int ReadCount => Volatile.Read(ref _readCount);

        protected void RecordRead() => Interlocked.Increment(ref _readCount);

        public virtual Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(CancellationToken cancellationToken)
        {
            RecordRead();
            return Task.FromResult<IReadOnlyList<AlertSourceObservation>>([]);
        }

        public IReadOnlyList<AlertSourceObservation> CreateUnavailableObservations(
            DateTimeOffset checkedAtUtc,
            string errorCode) => [];
    }

    private sealed class BlockingProvider : CountingProvider
    {
        private int _firstRead = 1;

        public TaskCompletionSource FirstReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRead { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<IReadOnlyList<AlertSourceObservation>> ReadAsync(
            CancellationToken cancellationToken)
        {
            RecordRead();
            if (Interlocked.Exchange(ref _firstRead, 0) == 1)
            {
                FirstReadStarted.TrySetResult();
                await ReleaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            return [];
        }
    }
}
