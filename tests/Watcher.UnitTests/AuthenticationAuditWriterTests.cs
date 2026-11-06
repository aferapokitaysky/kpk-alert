using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watcher.Api.Security;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.UnitTests;

public sealed class AuthenticationAuditWriterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 10, 15, 0, TimeSpan.Zero);

    [Fact]
    public async Task RecordAsync_persists_anonymous_rejection_reason()
    {
        await using WatcherDbContext db = CreateDbContext();

        await AuthenticationAuditWriter.RecordAsync(
            db,
            new FixedTimeProvider(Now),
            "auth.login.rejected",
            userId: null,
            "google",
            "email_unverified",
            CancellationToken.None);

        AuditEvent auditEvent = await db.AuditEvents.SingleAsync();
        Assert.Equal("auth.login.rejected", auditEvent.EventType);
        Assert.Equal("system", auditEvent.ActorType);
        Assert.Null(auditEvent.ActorId);
        Assert.Equal(Now, auditEvent.OccurredAtUtc);
        using var payload = JsonDocument.Parse(auditEvent.Payload);
        Assert.Equal("google", payload.RootElement.GetProperty("method").GetString());
        Assert.Equal("email_unverified", payload.RootElement.GetProperty("reason").GetString());
        Assert.Equal(2, payload.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task RecordAsync_persists_success_for_local_user_without_rejection_reason()
    {
        await using WatcherDbContext db = CreateDbContext();
        var userId = Guid.NewGuid();
        db.Users.Add(new UserAccount
        {
            Id = userId,
            ExternalSubject = "google-subject",
            Email = "user@example.test"
        });
        await db.SaveChangesAsync();

        await AuthenticationAuditWriter.RecordAsync(
            db,
            new FixedTimeProvider(Now),
            "auth.login.succeeded",
            userId,
            "google",
            cancellationToken: CancellationToken.None);

        AuditEvent auditEvent = await db.AuditEvents.SingleAsync();
        Assert.Equal("user", auditEvent.ActorType);
        Assert.Equal(userId, auditEvent.ActorId);
        Assert.Equal(Now, auditEvent.OccurredAtUtc);
        using var payload = JsonDocument.Parse(auditEvent.Payload);
        Assert.Equal("google", payload.RootElement.GetProperty("method").GetString());
        Assert.False(payload.RootElement.TryGetProperty("reason", out _));
    }

    private static WatcherDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<WatcherDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
