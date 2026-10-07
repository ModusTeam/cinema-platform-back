using Cinema.Application.Common.Constants;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Seats.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.LockSeat;

public class LockSeatCommandHandler(
    ISeatLockingService seatLockingService,
    ICurrentUserService currentUser
) : IRequestHandler<LockSeatCommand, Result<LockSeatResponse>>
{
    public async Task<Result<LockSeatResponse>> Handle(LockSeatCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure<LockSeatResponse>(new Error("Auth", "Unauthorized"));
        
        var result = await seatLockingService.LockSeatAsync(request.SessionId, request.SeatId, userId.Value, ct);
        
        if (result.IsFailure)
        {
            return Result.Failure<LockSeatResponse>(result.Error);
        }

        var response = new LockSeatResponse(
            Locked: true,
            Message: "Seat successfully locked.",
            ExpiresAt: DateTime.UtcNow.AddMinutes(OrderConstants.SeatLockDurationMinutes),
            SessionId: request.SessionId,
            SeatId: request.SeatId
        );
        
        return Result.Success(response);
    }
}
