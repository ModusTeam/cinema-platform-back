using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Commands;

public record UpdateMovieStatusCommand(Guid Id, MovieStatus Status) : IRequest<Result<Guid>>;