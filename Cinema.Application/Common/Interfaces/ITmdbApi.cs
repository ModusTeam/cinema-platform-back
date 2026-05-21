using Refit;
using Cinema.Application.Common.Models.Tmdb;

namespace Cinema.Application.Common.Interfaces;

public interface ITmdbApi
{
    [Get("/search/movie?language=uk-UA")]
    Task<TmdbSearchResponse> SearchMoviesAsync(
        string query,
        [AliasAs("api_key")] string apiKey,
        CancellationToken cancellationToken = default);

    [Get("/movie/{id}?language=uk-UA&append_to_response=credits,videos,release_dates")]
    Task<TmdbMovieDetails> GetMovieDetailsAsync(
        int id,
        [AliasAs("api_key")] string apiKey,
        CancellationToken cancellationToken = default);
}