using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Common.Behaviours;

public class IdempotencyBehavior<TRequest, TResponse>(
    IDistributedCache cache,
    ILogger<IdempotencyBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IIdempotentCommand
    where TResponse : class
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var cacheKey = $"idempotency:{request.RequestId}";
        
        var cachedResult = await cache.GetStringAsync(cacheKey, cancellationToken);
        if (cachedResult is not null)
        {
            logger.LogInformation("Returning cached result for idempotent request {RequestId}", request.RequestId);
            
            return JsonSerializer.Deserialize<TResponse>(cachedResult)!;
        }
        var response = await next();
        
        var options = new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) };
        var serialized = JsonSerializer.Serialize(response);
        
        await cache.SetStringAsync(cacheKey, serialized, options, cancellationToken);

        return response;
    }
}