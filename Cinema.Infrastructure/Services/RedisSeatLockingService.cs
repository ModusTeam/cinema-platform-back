using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

namespace Cinema.Infrastructure.Services;

public class RedisSeatLockingService : ISeatLockingService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ITicketNotifier _notifier;
    private readonly ILogger<RedisSeatLockingService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExtensionDuration = TimeSpan.FromMinutes(5);
    private const string KeyPrefix = "lock:session";
    
    private const string ValidateAndExtendScript = @"
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('expire', KEYS[1], ARGV[2])
        else
            return 0
        end";

    private const string UnlockScript = @"
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('del', KEYS[1])
        else
            return 0
        end";

    private string GetKey(Guid sessionId, Guid seatId) => $"{KeyPrefix}:{sessionId}:{seatId}";

    public RedisSeatLockingService(
        IConnectionMultiplexer redis,
        ITicketNotifier notifier,
        ILogger<RedisSeatLockingService> logger)
    {
        _redis = redis;
        _db = redis.GetDatabase();
        _notifier = notifier;
        _logger = logger;
        
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

    public async Task<IEnumerable<Guid>> GetLockedSeatsBySessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        var server = _redis.GetServer(_redis.GetEndPoints().First());
        var pattern = $"{KeyPrefix}:{sessionId}:*";
        var lockedSeatIds = new List<Guid>();

        await foreach (var key in server.KeysAsync(pattern: pattern))
        {
            var parts = key.ToString().Split(':');
            if (parts.Length == 4 && Guid.TryParse(parts[3], out var seatId))
            {
                lockedSeatIds.Add(seatId);
            }
        }
        
        return lockedSeatIds;
    }
    
    public async Task<Result> LockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);
        var value = userId.ToString();

        try
        {
            var isLocked = await _resiliencePipeline.ExecuteAsync(async token =>
                await _db.StringSetAsync(key, value, LockDuration, When.NotExists), ct);

            if (!isLocked)
            {
                var currentLockValue = await _db.StringGetAsync(key);
                if (currentLockValue == value)
                {
                    await _db.KeyExpireAsync(key, LockDuration);
                    return Result.Success();
                }
                
                await _notifier.NotifySeatLockedAsync(sessionId, seatId, userId, ct);
                
                return Result.Failure(new Error("Seat.Locked", "Seat is already reserved by another user."));
            }

            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error while locking seat {SeatId} for session {SessionId}", seatId, sessionId);
            return Result.Failure(new Error("Redis.Error", "System error while reserving seat."));
        }
    }
    
    public async Task<bool> ValidateAndExtendLockAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);
        var userValue = userId.ToString();

        try
        {
            var result = await _db.ScriptEvaluateAsync(ValidateAndExtendScript, 
                keys: [new RedisKey(key)], 
                values: [userValue, ExtensionDuration.TotalSeconds]);

            return (int)result == 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating and extending lock for seat {SeatId}", seatId);
            return false;
        }
    }

    public async Task<Result> UnlockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);
        
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async token => 
                await _db.ScriptEvaluateAsync(UnlockScript, keys: [new RedisKey(key)], values: [userId.ToString()]), ct);
            
            if (!result.IsNull && (int)result == 1)
            {
                _logger.LogInformation("Seat {SeatId} unlocked by user {UserId}", seatId, userId);
                await _notifier.NotifySeatUnlockedAsync(sessionId, seatId, ct);
            }
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error while unlocking seat {SeatId}", seatId);
            return Result.Failure(new Error("Redis.Error", "Failed to unlock seat."));
        }
    }

    public async Task<bool> ValidateLockAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);

        try
        {
            var lockValue = await _db.StringGetAsync(key);
            return lockValue.HasValue && lockValue == userId.ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error while validating lock for seat {SeatId}", seatId);
            return false;
        }
    }
    
    public async Task<IEnumerable<Guid>> GetLockedSeatsAsync(Guid sessionId, IEnumerable<Guid> seatIds, CancellationToken ct = default)
    {
        var batch = _db.CreateBatch();
    
        var tasks = new List<(Guid SeatId, Task<RedisValue> Task)>();

        foreach (var seatId in seatIds)
        {
            var key = GetKey(sessionId, seatId);
            tasks.Add((seatId, batch.StringGetAsync(key)));
        }

        batch.Execute();

        await Task.WhenAll(tasks.Select(x => x.Task));

        var lockedSeats = new List<Guid>();

        foreach (var (seatId, task) in tasks)
        {
            if (task.Result.HasValue)
            {
                lockedSeats.Add(seatId);
            }
        }

        return lockedSeats;
    }
}