using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.LockSeat;

public class LockSeatCommandHandler(
    ISeatLockingService seatLockingService,
    ICurrentUserService currentUser,
    ITicketNotifier notifier
) : IRequestHandler<LockSeatCommand, Result>
{
    public async Task<Result> Handle(LockSeatCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure(new Error("Auth", "Unauthorized"));

        var lockResult = await seatLockingService.LockSeatAsync(request.SessionId, request.SeatId, userId.Value, ct);
        
        if (lockResult.IsFailure)
            return lockResult;

        await notifier.NotifySeatLockedAsync(request.SessionId, request.SeatId, userId.Value, ct);

        return Result.Success();
    }
}