using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.CreateMovie;

public record CreateMovieCommand(
    string Title,
    string Description,
    int DurationMinutes,
    int ReleaseYear,
    MovieStatus Status
) : IRequest<Result<Guid>>;