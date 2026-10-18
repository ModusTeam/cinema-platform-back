using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;

namespace Cinema.Application.Orders.Commands.CreateOrder;

internal static class OrderReservationWorkflow
{
    public static async Task<Result<Guid>> ReserveAsync(
        IOrderReservationService reservationService,
        ISeatLockingService seatLockingService,
        Guid? userId,
        Guid sessionId,
        List<Guid> seatIds,
        CancellationToken ct)
    {
        if (userId is null)
            return Result.Failure<Guid>(new Error("Auth.Required", "User not authenticated"));

        foreach (Guid seatId in seatIds)
        {
            if (!await seatLockingService.ValidateAndExtendLockAsync(sessionId, seatId, userId.Value, ct))
            {
                await seatLockingService.UnlockSeatsAsync(sessionId, seatIds, userId.Value, ct);
                return Result.Failure<Guid>(new Error("Order.LockExpired", $"Lock expired for seat {seatId}."));
            }
        }

        try
        {
            Result<Guid> result = await reservationService.ReserveOrderAsync(userId.Value, sessionId, seatIds, ct);
            if (result.IsFailure)
                await seatLockingService.UnlockSeatsAsync(sessionId, seatIds, userId.Value, ct);
            return result;
        }
        catch
        {
            await seatLockingService.UnlockSeatsAsync(sessionId, seatIds, userId.Value, ct);
            throw;
        }
    }
}
