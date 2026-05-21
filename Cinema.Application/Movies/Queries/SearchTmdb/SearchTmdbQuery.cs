using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Application.Movies.Constants;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Movies.Queries.SearchTmdb;

public record TmdbSearchResult(int TmdbId, string Title, string Year, string? PosterUrl);

public record SearchTmdbQuery(string Query) : IRequest<List<TmdbSearchResult>>;

public class SearchTmdbQueryHandler(
    ITmdbApi tmdbApi,
    IMemoryCache cache,
    IOptions<TmdbSettings> settings,
    ILogger<SearchTmdbQueryHandler> logger
    ) 
    : IRequestHandler<SearchTmdbQuery, List<TmdbSearchResult>>
{
    public async Task<List<TmdbSearchResult>> Handle(SearchTmdbQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return [];

        var cacheKey = $"{TmdbConstants.SearchCacheKeyPrefix}{request.Query.Trim().ToLower()}";
        
        var result = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TmdbConstants.SearchCacheDuration;
        
            try 
            {
                if (string.IsNullOrEmpty(settings.Value.ApiKey))
                {
                    logger.LogError("TMDB API Key is missing in settings!");
                    return new List<TmdbSearchResult>();
                }

                var response = await tmdbApi.SearchMoviesAsync(request.Query, settings.Value.ApiKey);
                if (response?.Results == null) 
                {
                    logger.LogWarning("TMDB returned null results for query: {Query}", request.Query);
                    return new List<TmdbSearchResult>();
                }

                var imgBase = settings.Value.ImageBaseUrl;

                return response.Results.Select(r => new TmdbSearchResult(
                    r.Id,
                    r.Title,
                    !string.IsNullOrEmpty(r.ReleaseDate) && DateTime.TryParse(r.ReleaseDate, out var d) ? d.Year.ToString() : "N/A",
                    !string.IsNullOrEmpty(r.PosterPath) ? $"{imgBase}{r.PosterPath}" : null
                )).ToList();
            }
            catch (Refit.ApiException apiEx)
            {
                logger.LogError(apiEx, "TMDB API Error: {StatusCode} - {Content}", apiEx.StatusCode, apiEx.Content);
                return new List<TmdbSearchResult>();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to search TMDB for query: {Query}", request.Query);
                return new List<TmdbSearchResult>();
            }
        });

        return result ?? [];
    }
}