using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;

public record RenameMovieCommand(Guid Id, string NewTitle) : IRequest<Result<Guid>>;