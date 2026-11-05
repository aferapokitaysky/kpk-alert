namespace Watcher.Api.Contracts;

public sealed record CreateStopCommandRequest(IReadOnlyList<Guid>? LocationIds);
