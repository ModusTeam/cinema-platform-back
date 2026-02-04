using System.Net.Http.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Tmdb;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Services;

public class TmdbService : ITmdbService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;
    private readonly ILogger<TmdbService> _logger;

    public TmdbService(HttpClient httpClient, IConfiguration config, ILogger<TmdbService> logger)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri(config["Tmdb:BaseUrl"] ?? "https://api.themoviedb.org/3/");
        _apiKey = config["Tmdb:ApiKey"]!;
        _logger = logger;
    }

    public async Task<TmdbSearchResponse?> SearchMoviesAsync(string query)
    {
        var url = $"search/movie?api_key={_apiKey}&query={query}&language=uk-UA";
        try
        {
            return await _httpClient.GetFromJsonAsync<TmdbSearchResponse>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "TMDB Search Failed");
            return new TmdbSearchResponse();
        }
    }

    public async Task<TmdbMovieDetails?> GetMovieDetailsAsync(int tmdbId)
    {
        var url = $"movie/{tmdbId}?api_key={_apiKey}&language=uk-UA&append_to_response=credits,videos";
        
        try
        {
            return await _httpClient.GetFromJsonAsync<TmdbMovieDetails>(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching TMDB movie details for ID {Id}", tmdbId);
            return null;
        }
    }
}