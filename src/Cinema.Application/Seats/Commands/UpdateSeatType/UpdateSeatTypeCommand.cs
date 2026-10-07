using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.UpdateSeatType;

public record UpdateSeatTypeCommand(
    Guid Id,
    string Name,
    string? Description
) : IRequest<Result>;
