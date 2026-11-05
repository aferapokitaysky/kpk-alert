namespace Watcher.Domain.Entities;

public sealed class MinuteOfSilenceOccurrence
{
    public DateOnly RunDate { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string? SkipReason { get; set; }
    public Guid? CommandId { get; set; }
    public DateTimeOffset ProcessedAtUtc { get; set; }
}
