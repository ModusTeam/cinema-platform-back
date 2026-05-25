using Cinema.Application.Common.Interfaces;
using Cinema.Application.Seats.Dtos;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Seats.Commands.UnlockSeat;

public class UnlockSeatCommandHandler(
    ISeatLockingService seatLockingService,
    ICurrentUserService currentUser
) : IRequestHandler<UnlockSeatCommand, Result<UnlockSeatResponse>>
{
    public async Task<Result<UnlockSeatResponse>> Handle(UnlockSeatCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure<UnlockSeatResponse>(new Error("Auth", "Unauthorized"));
        
        var result = await seatLockingService.UnlockSeatAsync(request.SessionId, request.SeatId, userId.Value, ct);
        
        if (result.IsFailure)
        {
            return Result.Failure<UnlockSeatResponse>(result.Error);
        }

        var response = new UnlockSeatResponse(
            Unlocked: true,
            Message: "Seat successfully unlocked.",
            SessionId: request.SessionId,
            SeatId: request.SeatId
        );
        
        return Result.Success(response);
    }
}
