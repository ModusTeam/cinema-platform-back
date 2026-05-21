namespace Cinema.Api.DTOs.Movies;

public record TmdbSearchResultDto(
    int TmdbId,
    string Title,
    string Year,
    string? PosterUrl
);
