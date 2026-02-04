using Refit;
using Cinema.Application.Common.Models.Tmdb;

namespace Cinema.Application.Common.Interfaces;

public interface ITmdbApi
{
    [Get("/3/search/movie?language=uk-UA")]
    Task<TmdbSearchResponse> SearchMoviesAsync(
        [AliasAs("query")] string query, 
        [AliasAs("api_key")] string apiKey
    );

    [Get("/3/movie/{id}?language=uk-UA&append_to_response=credits,videos&include_video_language=uk,en")]
    Task<TmdbMovieDetails> GetMovieDetailsAsync(
        int id, 
        [AliasAs("api_key")] string apiKey
    );
}