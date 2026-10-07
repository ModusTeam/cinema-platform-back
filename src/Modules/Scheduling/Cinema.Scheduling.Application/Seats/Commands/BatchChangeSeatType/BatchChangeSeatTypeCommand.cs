using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.BatchChangeSeatType;

public record BatchChangeSeatTypeCommand(
    Guid HallId,
    List<Guid> SeatIds,
    Guid NewSeatTypeId
) : IRequest<Result>;