using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Catalog.Application.Movies.Commands.RefreshMovieFromTmdb;

public record RefreshMovieFromTmdbCommand(Guid Id) : IRequest<Result>;
