using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.LockSeat;

public record LockSeatCommand(Guid SessionId, Guid SeatId, Guid UserId) : IRequest<Result>;

public class LockSeatCommandHandler(ISeatLockingService seatLockingService) 
    : IRequestHandler<LockSeatCommand, Result>
{
    public async Task<Result> Handle(LockSeatCommand request, CancellationToken ct)
    {
        return await seatLockingService.LockSeatAsync(request.SessionId, request.SeatId, request.UserId, ct);
    }
}