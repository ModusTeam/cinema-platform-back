using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.UnlockSeat;

public class UnlockSeatCommandHandler(
    ISeatLockingService seatLockingService,
    ICurrentUserService currentUser
) : IRequestHandler<UnlockSeatCommand, Result>
{
    public async Task<Result> Handle(UnlockSeatCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure(new Error("Auth", "Unauthorized"));
        return await seatLockingService.UnlockSeatAsync(request.SessionId, request.SeatId, userId.Value, ct);
    }
}
