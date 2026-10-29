using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.OutputCaching;

namespace Cinema.Api.Filters;

public sealed class MovieCacheInvalidationFilter(IOutputCacheStore cache, ILogger<MovieCacheInvalidationFilter> logger)
    : IAsyncActionFilter
{
    public const string CacheTag = "catalog-movies";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        ActionExecutedContext executed = await next();
        int? status = executed.Result switch
        {
            ObjectResult result => result.StatusCode,
            StatusCodeResult result => result.StatusCode,
            _ => null
        };
        if (executed.Exception is not null || status is not (>= 200 and < 300))
            return;

        // The database is committed. Cache availability must not turn success into a failed mutation.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        try { await cache.EvictByTagAsync(CacheTag, timeout.Token); }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Could not invalidate the movie list cache after a successful mutation.");
        }
    }
}
