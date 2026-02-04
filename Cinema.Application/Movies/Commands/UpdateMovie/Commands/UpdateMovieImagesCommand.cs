using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Commands;

public record UpdateMovieImagesCommand(
    Guid Id, 
    string? PosterUrl, 
    string? BackdropUrl, 
    string? TrailerUrl
) : IRequest<Result<Guid>>;