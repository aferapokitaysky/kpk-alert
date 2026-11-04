using System.Text.Json;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Security;

internal static class AuthenticationAuditWriter
{
    public static async Task RecordAsync(
        WatcherDbContext db,
        TimeProvider clock,
        string eventType,
        Guid? userId,
        string method,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        object payload = reason is null ? new { method } : new { method, reason };
        db.AuditEvents.Add(new AuditEvent
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorType = userId.HasValue ? "user" : "system",
            ActorId = userId,
            Payload = JsonSerializer.Serialize(payload),
            OccurredAtUtc = clock.GetUtcNow()
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}
