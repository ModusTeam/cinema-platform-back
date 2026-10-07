using Refit;
using Cinema.Catalog.Application.Common.Models.Tmdb;

namespace Cinema.Catalog.Application.Common.Interfaces;

public interface ITmdbApi
{
    [Get("/search/movie?language=uk-UA")]
    Task<TmdbSearchResponse> SearchMoviesAsync(
        string query,
        [AliasAs("api_key")] string apiKey,
        CancellationToken cancellationToken = default);

    [Get("/movie/{id}")]
    Task<TmdbMovieDetails> GetMovieDetailsAsync(
        int id,
        [AliasAs("api_key")] string apiKey,
        [AliasAs("language")] string language,
        [AliasAs("append_to_response")] string appendToResponse,
        CancellationToken cancellationToken = default);
}
