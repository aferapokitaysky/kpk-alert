namespace Watcher.Api.Contracts;

public sealed record CreateAlertCommandRequest(
    string? Action,
    string? PresetCode,
    IReadOnlyList<Guid>? LocationIds);
