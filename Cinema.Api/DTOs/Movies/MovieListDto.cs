namespace Cinema.Api.DTOs.Movies;

public record MovieListDto(
    Guid Id,
    string Title,
    int ReleaseYear,
    decimal Rating,
    int DurationMinutes,
    string? PosterUrl,
    string? BackdropUrl,
    List<string> Genres,
    int Status,
    string? AgeRestriction
);
