namespace Watcher.Api.Contracts;

public sealed record DeviceConnectionStatusResponse(
    Guid DeviceId,
    string DeviceName,
    Guid LocationId,
    string LocationName,
    string ConnectionState);
