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

    public async Task<Guid> OpenSessionAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        await using IDbContextTransaction? transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken)
            : null;

        if (db.Database.IsNpgsql())
        {
            List<Device> lockedDevices = await db.Devices
                .FromSqlInterpolated($"SELECT * FROM devices WHERE \"Id\" = {deviceId} FOR UPDATE")
                .AsNoTracking()
                .ToListAsync(cancellationToken);
            if (lockedDevices.Count == 0)
            {
                throw new InvalidOperationException("Не знайдено зареєстрований пристрій для WebSocket-сеансу.");
            }
        }
        else if (!await db.Devices.AnyAsync(device => device.Id == deviceId, cancellationToken))
        {
            throw new InvalidOperationException("Не знайдено зареєстрований пристрій для WebSocket-сеансу.");
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        if (db.Database.IsRelational())
        {
            await db.DeviceSessions
                .Where(session => session.DeviceId == deviceId && session.DisconnectedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.DisconnectedAtUtc, now),
                    cancellationToken);
        }
        else
        {
            List<DeviceSession> activeSessions = await db.DeviceSessions
                .Where(session => session.DeviceId == deviceId && session.DisconnectedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (DeviceSession activeSession in activeSessions)
            {
                activeSession.DisconnectedAtUtc = now;
            }
        }

        var sessionId = Guid.NewGuid();
        db.DeviceSessions.Add(new DeviceSession
        {
            Id = sessionId,
            DeviceId = deviceId,
            ConnectedAtUtc = now,
            LastSeenAtUtc = now
        });
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
        if (db.Database.IsRelational())
        {
            await db.DeviceSessions
                .Where(session => session.Id == sessionId && session.DisconnectedAtUtc == null)
                .ExecuteUpdateAsync(update => update.SetProperty(session => session.DisconnectedAtUtc, now),
                    cancellationToken);
            return;
        }

        DeviceSession? session = await db.DeviceSessions.SingleOrDefaultAsync(
            item => item.Id == sessionId && item.DisconnectedAtUtc == null, cancellationToken);
        if (session is not null)
        {
            session.DisconnectedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<bool> IsSessionActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await using AsyncServiceScope scope = scopeFactory.CreateAsyncScope();
        WatcherDbContext db = scope.ServiceProvider.GetRequiredService<WatcherDbContext>();
        return await db.DeviceSessions.AsNoTracking()
            .AnyAsync(session => session.Id == sessionId && session.DisconnectedAtUtc == null,
                cancellationToken);
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
}
