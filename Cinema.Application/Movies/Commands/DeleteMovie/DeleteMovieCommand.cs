using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.DeleteMovie;

public record DeleteMovieCommand(Guid Id) : IRequest<Result>;