using Cinema.Application.Seats.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.UnlockSeat;

public record UnlockSeatCommand(Guid SessionId, Guid SeatId) : IRequest<Result<UnlockSeatResponse>>;
