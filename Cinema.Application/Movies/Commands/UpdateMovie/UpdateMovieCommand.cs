using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.UpdateMovie;

public abstract record UpdateMovieCommand(
    Guid Id,
    string Title,
    string? Description,
    string? PosterUrl,
    string? BackdropUrl,
    string? TrailerUrl
) : IRequest<Result>;