using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Genres.Commands.CreateGenre;

public record CreateGenreCommand(string Name, int? ExternalId) : IRequest<Result<Guid>>;