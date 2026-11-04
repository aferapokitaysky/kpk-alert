namespace Watcher.Domain.Entities;

public sealed class UserAccount
{
    public Guid Id { get; set; }
    public string ExternalSubject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAtUtc { get; set; }
}
