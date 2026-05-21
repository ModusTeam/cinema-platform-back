namespace Cinema.Api.DTOs.Movies;

public record MovieDto(
    Guid Id,
    string Title,
    string? Description,
    int DurationMinutes,
    decimal Rating,
    string? AgeRestriction,
    int ReleaseYear,
    string? PosterUrl,
    string? BackdropUrl,
    string? TrailerUrl,
    List<string> Genres,
    List<ActorDto> Cast,
    int Status
);

public record ActorDto(string Name, string? Role, string? PhotoUrl);
