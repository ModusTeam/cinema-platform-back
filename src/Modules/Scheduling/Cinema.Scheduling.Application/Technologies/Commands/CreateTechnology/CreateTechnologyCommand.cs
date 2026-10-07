using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Technologies.Commands.CreateTechnology;

public record CreateTechnologyCommand(string Name, string Type) : IRequest<Result<Guid>>;