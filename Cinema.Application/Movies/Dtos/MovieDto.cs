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
    List<ActorDto> Cast
)
{
    public static MovieDto FromDomain(Movie movie)
    {
        return new MovieDto(
            movie.Id.Value,
            movie.Title,
            movie.Description,
            movie.DurationMinutes,
            movie.Rating,
            movie.ReleaseYear,
            movie.PosterUrl,
            movie.BackdropUrl,
            movie.TrailerUrl,
            movie.MovieGenres.Select(mg => mg.Genre!.Name).ToList(),
            movie.Cast.Select(c => new ActorDto(c.Name, c.Role, c.PhotoUrl)).ToList()
        );
    }
}

public record ActorDto(string Name, string? Role, string? PhotoUrl);