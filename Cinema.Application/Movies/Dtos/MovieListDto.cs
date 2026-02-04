namespace Cinema.Application.Movies.Dtos;

public record MovieListDto(
    Guid Id,
    string Title,
    int ReleaseYear,
    decimal Rating,
    int DurationMinutes,
    string? PosterUrl,
    List<string> Genres,
    int Status
);