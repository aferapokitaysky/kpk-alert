using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Watcher.Domain.Entities;
using Watcher.Infrastructure.Persistence;

namespace Watcher.Api.Delivery;

public sealed class DeviceSessionPresenceService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<DeviceSessionPresenceService> logger)
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PresenceTimeToLive = TimeSpan.FromSeconds(20);
    private static readonly System.Text.Json.JsonSerializerOptions AuditJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

    public async Task<Guid> OpenSessionAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        await using IDbContextTransaction? transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        Device? device;
        if (db.Database.IsNpgsql())
        {
            List<Device> lockedDevices = await db.Devices
                .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            device = lockedDevices.SingleOrDefault();
        }
        else
        {
            device = await db.Devices.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == deviceId, cancellationToken);
        }

        if (device is null)
        {
            throw new DeviceSessionUnavailableException(
                "Не знайдено зареєстрований пристрій для WebSocket-сеансу.");
        }

        if (device.RevokedAtUtc is not null ||
            !await db.Locations.AsNoTracking().AnyAsync(
                location => location.Id == device.LocationId && location.IsActive,
                cancellationToken))
        {
            throw new DeviceSessionUnavailableException(
                "Пристрій відкликано або його корпус неактивний.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        DeviceSession[] activeSessions;
        if (db.Database.IsNpgsql())
        {
            activeSessions = await db.DeviceSessions
                .FromSqlInterpolated($"SELECT * FROM device_sessions WHERE \"DeviceId\" = {deviceId} AND \"DisconnectedAtUtc\" IS NULL FOR UPDATE")
                .ToArrayAsync(cancellationToken);
        }
        else
        {
            activeSessions = await db.DeviceSessions
                .Where(session => session.DeviceId == deviceId && session.DisconnectedAtUtc == null)
                .ToArrayAsync(cancellationToken);
        }

        foreach (DeviceSession activeSession in activeSessions)
        {
            activeSession.DisconnectedAtUtc = now;
            db.AuditEvents.Add(CreateSessionAuditEvent(
                "device.session_superseded", activeSession, device, now));
        }

        var sessionId = Guid.NewGuid();
        db.DeviceSessions.Add(new DeviceSession
        {
            Id = sessionId,
            DeviceId = deviceId,
            ConnectedAtUtc = now,
            LastSeenAtUtc = now
        });
        DeviceSession openedSession = db.DeviceSessions.Local.Single(session => session.Id == sessionId);
        db.AuditEvents.Add(CreateSessionAuditEvent("device.session_connected", openedSession, device, now));
        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return sessionId;
    }

    public async Task RunHeartbeatAsync(
        Guid sessionId,
        Action sessionSuperseded,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!await RefreshSessionAsync(sessionId, cancellationToken))
                {
                    sessionSuperseded();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Heartbeat сеансу пристрою {SessionId} зупинено.", sessionId);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception,
                "Не вдалося оновити присутність пристрою для сеансу {SessionId}.", sessionId);
            sessionSuperseded();
        }
    }

    public async Task DisconnectAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        DateTimeOffset now = timeProvider.GetUtcNow();
        await using IDbContextTransaction? transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        DeviceSession? session = db.Database.IsNpgsql()
            ? await db.DeviceSessions
                .FromSqlInterpolated($"SELECT * FROM device_sessions WHERE \"Id\" = {sessionId} AND \"DisconnectedAtUtc\" IS NULL FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
            : await db.DeviceSessions.SingleOrDefaultAsync(
                item => item.Id == sessionId && item.DisconnectedAtUtc == null, cancellationToken);
        if (session is null)
        {
            return;
        }

        session.DisconnectedAtUtc = now;
        Device? device = await db.Devices.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == session.DeviceId, cancellationToken);
        if (device is not null)
        {
            db.AuditEvents.Add(CreateSessionAuditEvent(
                "device.session_disconnected", session, device, now));
        }

        await db.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        return await (from session in db.DeviceSessions.AsNoTracking()
                      join device in db.Devices.AsNoTracking() on session.DeviceId equals device.Id
                      join location in db.Locations.AsNoTracking() on device.LocationId equals location.Id
                      where session.Id == sessionId && session.DisconnectedAtUtc == null &&
                            device.RevokedAtUtc == null && location.IsActive
                      select session.Id)
            .AnyAsync(cancellationToken);
    }

    public static IQueryable<Guid> ConnectedDeviceIds(WatcherDbContext db, DateTimeOffset now) =>
        db.DeviceSessions.AsNoTracking()
            .Where(session => session.DisconnectedAtUtc == null &&
                              session.LastSeenAtUtc >= now - PresenceTimeToLive)
            .Select(session => session.DeviceId)
            .Distinct();

    private async Task<bool> RefreshSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        DateTimeOffset now = timeProvider.GetUtcNow();
        if (db.Database.IsRelational())
        {
            var updated = await db.DeviceSessions
                .Where(session => session.Id == sessionId && session.DisconnectedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.LastSeenAtUtc, now),
                    cancellationToken);
            return updated == 1;
        }

        DeviceSession? session = await db.DeviceSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId && item.DisconnectedAtUtc == null, cancellationToken);
        if (session is null)
        {
            return false;
        }

        session.LastSeenAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static AuditEvent CreateSessionAuditEvent(
        string eventType,
        DeviceSession session,
        Device device,
        DateTimeOffset occurredAtUtc) => new()
        {
            Id = Guid.NewGuid(),
            EventType = eventType,
            ActorType = "device",
            ActorId = device.Id,
            Payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                session.Id,
                DeviceId = device.Id,
                device.LocationId,
                OccurredAtUtc = occurredAtUtc
            }, AuditJsonOptions),
            OccurredAtUtc = occurredAtUtc
        };
}

public sealed class DeviceSessionUnavailableException(string message) : InvalidOperationException(message);
