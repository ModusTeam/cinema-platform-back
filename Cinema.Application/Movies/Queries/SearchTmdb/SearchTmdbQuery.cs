using Cinema.Application.Common.Interfaces;
using MediatR;
using Microsoft.Extensions.Configuration;

namespace Cinema.Application.Movies.Queries.SearchTmdb;

public record TmdbSearchResultDto(int TmdbId, string Title, string Year, string? PosterUrl);

public record SearchTmdbQuery(string Query) : IRequest<List<TmdbSearchResultDto>>;

public class SearchTmdbQueryHandler(ITmdbService tmdbService, IConfiguration config) 
    : IRequestHandler<SearchTmdbQuery, List<TmdbSearchResultDto>>
{
    public async Task<List<TmdbSearchResultDto>> Handle(SearchTmdbQuery request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Query)) return [];

        var response = await tmdbService.SearchMoviesAsync(request.Query);
        var imgBase = config["Tmdb:ImageBaseUrl"];

        return response?.Results.Select(r => new TmdbSearchResultDto(
            r.Id,
            r.Title,
            DateTime.TryParse(r.ReleaseDate, out var d) ? d.Year.ToString() : "N/A",
            !string.IsNullOrEmpty(r.PosterPath) ? $"{imgBase}{r.PosterPath}" : null
        )).ToList() ?? [];
    }
}