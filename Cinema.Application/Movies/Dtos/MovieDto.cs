using Cinema.Domain.Entities;

namespace Cinema.Application.Movies.Dtos;

public record MovieDto(
    Guid Id,
    string Title,
    string? Description,
    int DurationMinutes,
    decimal Rating,
    int ReleaseYear,
    string? PosterUrl,
    string? BackdropUrl,
    string? TrailerUrl,
    List<string> Genres,
    List<ActorDto> Cast,
    int Status
);

public record ActorDto(string Name, string? Role, string? PhotoUrl);