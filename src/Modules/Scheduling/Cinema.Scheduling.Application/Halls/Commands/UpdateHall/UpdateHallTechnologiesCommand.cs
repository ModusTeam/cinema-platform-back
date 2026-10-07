using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Commands.UpdateHall;

public record UpdateHallTechnologiesCommand(Guid HallId, List<Guid> TechnologyIds) : IRequest<Result>;