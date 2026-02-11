using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Tmdb;
using Cinema.Application.Common.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Refit;

namespace Cinema.Infrastructure.Services;

public class TmdbService(
    ITmdbApi tmdbApi, 
    IOptions<TmdbSettings> settings, 
    ILogger<TmdbService> logger) : ITmdbService
{
    private readonly TmdbSettings _settings = settings.Value;

    public async Task<TmdbSearchResponse?> SearchMoviesAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new TmdbSearchResponse();

        try
        {
            return await tmdbApi.SearchMoviesAsync(query, _settings.ApiKey);
        }
        catch (ApiException ex)
        {
            logger.LogError(ex, "TMDB API Error: {StatusCode} - {Content}", ex.StatusCode, ex.Content);
            return new TmdbSearchResponse();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error searching TMDB for query: {Query}", query);
            return new TmdbSearchResponse();
        }
    }

    public async Task<TmdbMovieDetails?> GetMovieDetailsAsync(int tmdbId)
    {
        try
        {
            return await tmdbApi.GetMovieDetailsAsync(tmdbId, _settings.ApiKey);
        }
        catch (ApiException ex)
        {
            if (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                logger.LogWarning("Movie with TMDB ID {Id} not found.", tmdbId);
                return null;
            }

            logger.LogError(ex, "TMDB API Error fetching details for ID {Id}: {StatusCode}", tmdbId, ex.StatusCode);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error fetching TMDB details for ID {Id}", tmdbId);
            return null;
        }
    }
}