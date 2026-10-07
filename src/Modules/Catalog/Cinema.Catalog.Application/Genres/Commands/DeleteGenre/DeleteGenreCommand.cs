using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Catalog.Application.Genres.Commands.DeleteGenre;

public record DeleteGenreCommand(Guid Id) : IRequest<Result>;