using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using MediatR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Movies.Queries.SearchTmdb;

public record TmdbSearchResultDto(int TmdbId, string Title, string Year, string? PosterUrl);

public record SearchTmdbQuery(string Query) : IRequest<List<TmdbSearchResultDto>>;

public class SearchTmdbQueryHandler(
    ITmdbApi tmdbApi,
    IMemoryCache cache,
    IOptions<TmdbSettings> settings,
    ILogger<SearchTmdbQueryHandler> logger
    ) 
    : IRequestHandler<SearchTmdbQuery, List<TmdbSearchResultDto>>
{
    public async Task<List<TmdbSearchResultDto>> Handle(SearchTmdbQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return [];

        var cacheKey = $"tmdb-search-{request.Query.Trim().ToLower()}";
        
        var result = await cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
        
            try 
            {
                if (string.IsNullOrEmpty(settings.Value.ApiKey))
                {
                    logger.LogError("TMDB API Key is missing in settings!");
                    return new List<TmdbSearchResultDto>();
                }

                var response = await tmdbApi.SearchMoviesAsync(request.Query, settings.Value.ApiKey);
                if (response?.Results == null) 
                {
                    logger.LogWarning("TMDB returned null results for query: {Query}", request.Query);
                    return new List<TmdbSearchResultDto>();
                }

                var imgBase = settings.Value.ImageBaseUrl;

                return response.Results.Select(r => new TmdbSearchResultDto(
                    r.Id,
                    r.Title,
                    !string.IsNullOrEmpty(r.ReleaseDate) && DateTime.TryParse(r.ReleaseDate, out var d) ? d.Year.ToString() : "N/A",
                    !string.IsNullOrEmpty(r.PosterPath) ? $"{imgBase}{r.PosterPath}" : null
                )).ToList();
            }
            catch (Refit.ApiException apiEx)
            {
                logger.LogError(apiEx, "TMDB API Error: {StatusCode} - {Content}", apiEx.StatusCode, apiEx.Content);
                return new List<TmdbSearchResultDto>();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to search TMDB for query: {Query}", request.Query);
                return new List<TmdbSearchResultDto>();
            }
        });

        return result ?? [];
    }
}