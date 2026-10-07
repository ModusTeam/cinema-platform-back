using Cinema.Application.Common.Interfaces;
using Cinema.Application.Seats.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.LockSeat;

public record LockSeatCommand(Guid SessionId, Guid SeatId) : IRequest<Result<LockSeatResponse>>;
