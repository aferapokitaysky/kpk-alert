namespace Watcher.Api.Commands;

public interface ILocationCommandSequenceAllocator
{
    Task<long> AllocateAsync(Guid locationId, CancellationToken cancellationToken);
}
