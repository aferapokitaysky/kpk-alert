namespace Watcher.Api.Delivery;

public sealed class DeviceCommandTransportOptions
{
    public const string SectionName = "DeviceCommandTransport";

    public bool Enabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan AcknowledgementTimeout { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromSeconds(15);
    public int BatchSize { get; set; } = 50;

    public bool IsValid() =>
        PollInterval >= TimeSpan.FromMilliseconds(50) &&
        PollInterval <= TimeSpan.FromSeconds(5) &&
        AcknowledgementTimeout >= TimeSpan.FromSeconds(1) &&
        AcknowledgementTimeout <= TimeSpan.FromMinutes(1) &&
        InitialRetryDelay >= TimeSpan.FromMilliseconds(100) &&
        MaximumRetryDelay >= InitialRetryDelay &&
        MaximumRetryDelay <= TimeSpan.FromMinutes(1) &&
        BatchSize is >= 1 and <= 500;
}
