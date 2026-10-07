using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Catalog.Application.Genres.Commands.UpdateGenre;

public record UpdateGenreCommand(Guid Id, string Name) : IRequest<Result>;