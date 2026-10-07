using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Halls.Commands.DeleteHall;

public record DeleteHallCommand(Guid HallId) : IRequest<Result>;