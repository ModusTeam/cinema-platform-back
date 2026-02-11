using Refit;
using Cinema.Application.Common.Models.Tmdb;

namespace Cinema.Application.Common.Interfaces;

public interface ITmdbApi
{
    [Get("/search/movie?language=uk-UA")]
    Task<TmdbSearchResponse> SearchMoviesAsync(string query, [AliasAs("api_key")] string apiKey);

    [Get("/movie/{id}?language=uk-UA&append_to_response=credits,videos")]
    Task<TmdbMovieDetails> GetMovieDetailsAsync(int id, [AliasAs("api_key")] string apiKey);
}