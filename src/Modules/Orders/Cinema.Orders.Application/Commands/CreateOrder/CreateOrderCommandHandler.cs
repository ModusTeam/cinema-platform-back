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
        Guid? userId = currentUser.UserId;
        Result<Guid> reservationResult = await OrderReservationWorkflow.ReserveAsync(
            reservationService, seatLockingService, userId, request.SessionId, request.SeatIds, ct);
        if (reservationResult.IsFailure)
            return reservationResult;

        Guid orderId = reservationResult.Value;
        var checkoutResult = await checkoutOrchestrator.ProcessCheckoutAsync(
            userId!.Value,
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
