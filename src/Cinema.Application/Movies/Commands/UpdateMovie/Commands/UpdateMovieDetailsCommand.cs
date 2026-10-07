using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Commands;

public record UpdateMovieDetailsCommand(
    Guid Id,
    string? Description,
    int? DurationMinutes,
    double? Rating, 
    int? ReleaseYear
) : IRequest<Result<Guid>>;