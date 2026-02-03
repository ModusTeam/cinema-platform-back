using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Genres.Commands.UpdateGenre;

public record UpdateGenreCommand(int ExternalId, string Name) : IRequest<Result>;