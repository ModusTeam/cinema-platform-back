using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.RestoreMovie;

public record RestoreMovieCommand(Guid Id) : IRequest<Result>;
