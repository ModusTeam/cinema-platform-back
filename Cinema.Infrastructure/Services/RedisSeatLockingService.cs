using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using Microsoft.Extensions.Caching.Distributed;

namespace Cinema.Infrastructure.Services;

public class RedisSeatLockingService(IDistributedCache cache) : ISeatLockingService
{
    private const int LockTimeMinutes = 10;

    public async Task<Result> LockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);
        
        var currentLockOwner = await cache.GetStringAsync(key, token: ct);
        
        if (!string.IsNullOrEmpty(currentLockOwner) && currentLockOwner != userId.ToString())
        {
            return Result.Failure(new Error("Seat.Locked", " Seat is already locked."));
        }
        
        var options = new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(LockTimeMinutes)
        };
        
        await cache.SetStringAsync(key, userId.ToString(), options, token: ct);

        return Result.Success();
    }

    public async Task<Result> UnlockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);
        var currentLockOwner = await cache.GetStringAsync(key, token: ct);
        
        if (string.IsNullOrEmpty(currentLockOwner) || currentLockOwner != userId.ToString())
        {
            return Result.Success();
        }

        await cache.RemoveAsync(key, token: ct);
        return Result.Success();
    }

    private static string GetKey(Guid sessionId, Guid seatId) 
        => $"lock:s:{sessionId}:st:{seatId}";
}