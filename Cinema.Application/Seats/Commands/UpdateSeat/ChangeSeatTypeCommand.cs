using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.UpdateSeat;

public record ChangeSeatTypeCommand(
    Guid SeatId,
    Guid NewSeatTypeId
) : IRequest<Result>;