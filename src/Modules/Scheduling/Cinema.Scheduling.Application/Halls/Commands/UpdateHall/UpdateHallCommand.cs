using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Commands.UpdateHall;

public record UpdateHallCommand(
    Guid HallId,
    string Name
) : IRequest<Result>;