namespace Watcher.Api.Delivery;

public sealed class CommandOutboxWorkerOptions
{
    public const string SectionName = "CommandOutboxWorker";
    public static readonly TimeSpan MinimumPollInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaximumPollInterval = TimeSpan.FromMinutes(5);
    public const int MaximumBatchSize = 500;

    public bool Enabled { get; set; } = true;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; set; } = 50;

    public bool IsValid() =>
        PollInterval >= MinimumPollInterval &&
        PollInterval <= MaximumPollInterval &&
        BatchSize is >= 1 and <= MaximumBatchSize;
}
