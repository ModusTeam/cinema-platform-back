using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Catalog.Application.Movies.Commands.DeleteMovie;

public record DeleteMovieCommand(Guid Id) : IRequest<Result>;