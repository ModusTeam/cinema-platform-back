using Cinema.Application.Common.Models.Tmdb;

namespace Cinema.Application.Common.Interfaces;

public interface ITmdbService
{
    Task<TmdbSearchResponse?> SearchMoviesAsync(string query);
    Task<TmdbMovieDetails?> GetMovieDetailsAsync(int tmdbId);
}