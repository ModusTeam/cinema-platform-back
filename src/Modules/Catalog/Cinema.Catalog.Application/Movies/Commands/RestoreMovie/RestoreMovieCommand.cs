using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Catalog.Application.Movies.Commands.RestoreMovie;

public record RestoreMovieCommand(Guid Id) : IRequest<Result>;
