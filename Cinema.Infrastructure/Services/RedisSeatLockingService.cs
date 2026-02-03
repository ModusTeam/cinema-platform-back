using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

namespace Cinema.Infrastructure.Services;

public class RedisSeatLockingService : ISeatLockingService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly ResiliencePipeline _resiliencePipeline;
    private readonly ITicketNotifier _notifier;
    
    private const int LockTimeMinutes = 10; 

    public RedisSeatLockingService(
        IConnectionMultiplexer redis,
        ITicketNotifier notifier)
    {
        _redis = redis;
        _notifier = notifier;
        
        _resiliencePipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                ShouldHandle = new PredicateBuilder()
                    .Handle<RedisTimeoutException>()
                    .Handle<RedisConnectionException>(),
                MaxRetryAttempts = 3,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            })
            .AddTimeout(TimeSpan.FromSeconds(2))
            .Build();
    }

    public async Task<Result> LockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = GetKey(sessionId, seatId);
        var expiry = TimeSpan.FromMinutes(LockTimeMinutes);

        try
        {
            var success = await _resiliencePipeline.ExecuteAsync(async token => 
                await db.StringSetAsync(key, userId.ToString(), expiry, When.NotExists), ct);

            if (!success)
            {
                return Result.Failure(new Error("Seat.Locked", "Seat is already locked."));
            }

            await _notifier.NotifySeatLockedAsync(sessionId, seatId, userId, ct);

            return Result.Success();
        }
        catch (Exception)
        {
            return Result.Failure(new Error("Redis.Error", "Temporary system failure."));
        }
    }

    public async Task<Result> UnlockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = GetKey(sessionId, seatId);
        
        var script = @"
            if redis.call('get', KEYS[1]) == ARGV[1] then
                return redis.call('del', KEYS[1])
            else
                return 0
            end";

        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async token => 
                await db.ScriptEvaluateAsync(script, new RedisKey[] { key }, new RedisValue[] { userId.ToString() }), ct);
            
            if (!result.IsNull && (int)result == 1)
            {
                await _notifier.NotifySeatUnlockedAsync(sessionId, seatId, ct);
            }
            
            return Result.Success();
        }
        catch (Exception)
        {
            return Result.Failure(new Error("Redis.Error", "Failed to unlock seat."));
        }
    }

    public async Task<bool> ValidateLockAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var db = _redis.GetDatabase();
        var key = GetKey(sessionId, seatId);
        var lockValue = await db.StringGetAsync(key);
        
        return lockValue.HasValue && lockValue == userId.ToString();
    }

    private static string GetKey(Guid sessionId, Guid seatId) => $"lock:session:{sessionId}:seat:{seatId}";
}