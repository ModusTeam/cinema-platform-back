using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.ImportMovie;

public record ImportMovieCommand(int TmdbId) : IRequest<Result<Guid>>;