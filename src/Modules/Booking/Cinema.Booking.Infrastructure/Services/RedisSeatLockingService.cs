using Cinema.Application.Common.Constants;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

namespace Cinema.Booking.Infrastructure.Services;

public class RedisSeatLockingService : ISeatLockingService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly IDatabase _db;
    private readonly ITicketNotifier _notifier;
    private readonly ILogger<RedisSeatLockingService> _logger;
    private readonly ResiliencePipeline _resiliencePipeline;
    
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(OrderConstants.SeatLockDurationMinutes);
    private static readonly TimeSpan ExtensionDuration = TimeSpan.FromMinutes(5);
    private const string KeyPrefix = "lock:session";
    private const string SetPrefix = "locked_seats:session";
    
    private const string ValidateAndExtendScript = @"
        if redis.call('get', KEYS[1]) == ARGV[1] then
            return redis.call('expire', KEYS[1], ARGV[2])
        else
            return 0
        end";

    private const string LockScript = @"
        if redis.call('set', KEYS[1], ARGV[1], 'EX', ARGV[2], 'NX') then
            redis.call('sadd', KEYS[2], ARGV[3])
            return 1
        end
        return 0";

    private const string UnlockScript = @"
        if redis.call('get', KEYS[1]) == ARGV[1] then
            redis.call('del', KEYS[1])
            redis.call('srem', KEYS[2], ARGV[2])
            return 1
        else
            return 0
        end";

    private const string BatchUnlockScript = @"
        local removed = {}
        for i = 2, #KEYS do
            local key = KEYS[i]
            if redis.call('get', key) == ARGV[1] then
                redis.call('del', key)
                redis.call('srem', KEYS[1], ARGV[i])
                table.insert(removed, ARGV[i])
            end
        end
        return removed";

    private string GetKey(Guid sessionId, Guid seatId) => $"{KeyPrefix}:{{{sessionId}}}:{seatId}";
    private string GetSetKey(Guid sessionId) => $"{SetPrefix}:{{{sessionId}}}";

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
       var setKey = GetSetKey(sessionId);

       try
       {
           var seatIdsInSet = await _resiliencePipeline.ExecuteAsync(async token => 
               await _db.SetMembersAsync(setKey), ct);
           
           if (seatIdsInSet.Length == 0) return [];
           
           var keys = seatIdsInSet.Select(id => new RedisKey(GetKey(sessionId, Guid.Parse(id.ToString())))).ToArray();
           var values = await _resiliencePipeline.ExecuteAsync(async token => await _db.StringGetAsync(keys), ct);
           
           var activeSeats = new List<Guid>();
           var expiredSeats = new List<RedisValue>();

           for (var i = 0; i < values.Length; i++)
           {
               if(values[i].HasValue) activeSeats.Add(Guid.Parse(seatIdsInSet[i].ToString()));
               else expiredSeats.Add(seatIdsInSet[i]);
           }
           
           if(expiredSeats.Count > 0) await _db.SetRemoveAsync(setKey, expiredSeats.ToArray());
           return activeSeats;
           
       }
       catch (Exception e)
       {
           _logger.LogError(e, "Redis error while getting locked seats for session {SessionId}", sessionId);
           return [];
       }
    }
    
    public async Task<Result> LockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
    {
        var key = GetKey(sessionId, seatId);
        var value = userId.ToString();

        try
        {
            var lockResult = await _resiliencePipeline.ExecuteAsync(async token =>
                await _db.ScriptEvaluateAsync(LockScript,
                    keys: [new RedisKey(key), new RedisKey(GetSetKey(sessionId))],
                    values: [value, (int)LockDuration.TotalSeconds, seatId.ToString()]), ct);

            if ((int)lockResult == 1)
            {
                try
                {
                    await _notifier.NotifySeatLockedAsync(sessionId, seatId, userId, ct);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Notification failed on lock");
                }
                
                return Result.Success();
            }

            var extended = await _resiliencePipeline.ExecuteAsync(async token =>
                await _db.ScriptEvaluateAsync(ValidateAndExtendScript,
                    keys: [new RedisKey(key)],
                    values: [value, (int)LockDuration.TotalSeconds]), ct);
            if ((int)extended == 1) return Result.Success();
            
            return Result.Failure(new Error("Seat.AlreadyLocked", "Seat is already locked."));
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
                values: [userValue, (int)ExtensionDuration.TotalSeconds]);

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
                await _db.ScriptEvaluateAsync(UnlockScript,
                    keys: [new RedisKey(key), new RedisKey(GetSetKey(sessionId))],
                    values: [userId.ToString(), seatId.ToString()]), ct);
            
            if (!result.IsNull && (int)result == 1)
            {
                _logger.LogInformation("Seat {SeatId} unlocked by user {UserId}", seatId, userId);

                try
                {
                    await _notifier.NotifySeatUnlockedAsync(sessionId, seatId, ct);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Notification failed on unlock");
                }
            }
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error while unlocking seat {SeatId}", seatId);
            return Result.Failure(new Error("Redis.Error", "Failed to unlock seat."));
        }
    }

    public async Task<Result> UnlockSeatsAsync(Guid sessionId, IEnumerable<Guid> seatIds, Guid userId, CancellationToken ct = default)
    {
        var seatIdsList = seatIds.ToList();
        if (seatIdsList.Count == 0) return Result.Success();

        var keys = new[] { new RedisKey(GetSetKey(sessionId)) }
            .Concat(seatIdsList.Select(seatId => new RedisKey(GetKey(sessionId, seatId)))).ToArray();
        var values = new[] { (RedisValue)userId.ToString() }
            .Concat(seatIdsList.Select(seatId => (RedisValue)seatId.ToString())).ToArray();
        
        try
        {
            var result = await _resiliencePipeline.ExecuteAsync(async token => 
                await _db.ScriptEvaluateAsync(BatchUnlockScript, keys: keys, values: values), ct);
            
            RedisResult[] removed = (RedisResult[])result!;
            if (removed.Length > 0)
            {
                Guid[] removedSeatIds = removed.Select(id => Guid.Parse((string)id!)).ToArray();
                
                _logger.LogInformation("{Count} seats unlocked by user {UserId}", removedSeatIds.Length, userId);

                try
                {
                    var tasks = removedSeatIds.Select(seatId => _notifier.NotifySeatUnlockedAsync(sessionId, seatId, ct));
                    await Task.WhenAll(tasks);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Notification failed on batch unlock");
                }
            }
            
            return Result.Success();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Redis error while batch unlocking seats for session {SessionId}", sessionId);
            return Result.Failure(new Error("Redis.Error", "Failed to unlock seats."));
        }
    }

    /*public async Task<bool> ValidateLockAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default)
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
    }*/
    
    public async Task<IEnumerable<Guid>> GetLockedSeatsAsync(Guid sessionId, IEnumerable<Guid> seatIds, CancellationToken ct = default)
    {
        var seatIdArray = seatIds.ToArray();
        var keys = seatIdArray.Select(id => new RedisKey(GetKey(sessionId, id))).ToArray();

        try
        {
            var values = await _resiliencePipeline.ExecuteAsync(async token =>
                await _db.StringGetAsync(keys), ct);
            
            var lockedSeats = new List<Guid>();
            
            for (var i = 0; i < values.Length; i++)
            {
                if (values[i].HasValue) lockedSeats.Add(seatIdArray[i]);
            }
            
            return lockedSeats;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Redis error while getting locked seats for session {SessionId}", sessionId);
            return [];
        }
    }
}
