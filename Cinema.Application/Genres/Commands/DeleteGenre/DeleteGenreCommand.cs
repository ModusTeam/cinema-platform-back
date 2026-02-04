using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Genres.Commands.DeleteGenre;

public record DeleteGenreCommand(int ExternalId) : IRequest<Result>;