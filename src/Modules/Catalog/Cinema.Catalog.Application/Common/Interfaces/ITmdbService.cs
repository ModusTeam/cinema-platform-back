using Cinema.Catalog.Application.Common.Models.Tmdb;

namespace Cinema.Catalog.Application.Common.Interfaces;

public interface ITmdbService
{
    Task<TmdbSearchResponse?> SearchMoviesAsync(string query);
    Task<TmdbMovieDetails?> GetMovieDetailsAsync(int tmdbId);
}