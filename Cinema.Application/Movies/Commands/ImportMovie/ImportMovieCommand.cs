using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Movies.Commands.ImportMovie;

public record ImportMovieCommand(int TmdbId, string? AgeRestrictionOverride = null) : IRequest<Result<Guid>>;