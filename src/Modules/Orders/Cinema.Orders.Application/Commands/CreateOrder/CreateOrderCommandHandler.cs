using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public class CreateOrderCommandHandler(
    IOrderReservationService reservationService,
    ICurrentUserService currentUser,
    ISeatLockingService seatLockingService,
    IOrderCheckoutOrchestrator checkoutOrchestrator,
    ILogger<CreateOrderCommandHandler> logger) : IRequestHandler<CreateOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null)
            return Result.Failure<Guid>(new Error("Auth.Required", "User not authenticated"));

        foreach (var seatId in request.SeatIds)
        {
            if (!await seatLockingService.ValidateAndExtendLockAsync(request.SessionId, seatId, userId.Value, ct))
                return Result.Failure<Guid>(new Error("Order.LockExpired", $"Lock expired for seat {seatId}."));
        }

        var reservationResult = await reservationService.ReserveOrderAsync(userId.Value, request.SessionId, request.SeatIds, ct);
        if (reservationResult.IsFailure)
        {
            await seatLockingService.UnlockSeatsAsync(request.SessionId, request.SeatIds, userId.Value, ct);
            return reservationResult;
        }

        var orderId = reservationResult.Value;

        var checkoutResult = await checkoutOrchestrator.ProcessCheckoutAsync(
            userId.Value,
            request.SessionId,
            request.SeatIds,
            orderId,
            request.ApplyGoldUpgrade,
            request.UseLoyaltyPoints,
            request.PaymentToken,
            ct);

        if (checkoutResult.IsFailure)
        {
            logger.LogWarning("Checkout failed for Order {OrderId} by User {UserId}. Error: {Error}", orderId, userId.Value, checkoutResult.Error);
        }

        return checkoutResult;
    }
}