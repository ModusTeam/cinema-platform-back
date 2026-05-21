using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Events;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public class CreateOrderCommandHandler(
    IOrderReservationService reservationService,
    ICurrentUserService currentUser,
    ISeatLockingService seatLockingService,
    IPaymentService paymentService,
    ILoyaltyService loyaltyService,
    IApplicationDbContext context,
    IPublisher publisher,
    ILogger<CreateOrderCommandHandler> logger) : IRequestHandler<CreateOrderCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CreateOrderCommand request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null) return Result.Failure<Guid>(new Error("Auth.Required", "User not authenticated"));
        
        foreach (var seatId in request.SeatIds)
        {
            if (!await seatLockingService.ValidateAndExtendLockAsync(request.SessionId, seatId, userId.Value, ct))
            {
                 return Result.Failure<Guid>(new Error("Order.LockExpired", $"Lock expired for seat {seatId}."));
            }
        }
        
        var reservationResult = await reservationService.ReserveOrderAsync(userId.Value, request.SessionId, request.SeatIds, ct);
        if (reservationResult.IsFailure)
            return reservationResult;

        var orderId = reservationResult.Value;
        
        var orderAmount = await context.Orders
            .Where(o => o.Id == new EntityId<Order>(orderId))
            .Select(o => o.TotalAmount)
            .FirstOrDefaultAsync(ct);
        
        decimal amountToPay = orderAmount;
        int pointsToDeduct = 0;

        if (request.UseLoyaltyPoints)
        {
            var isLoyaltyAllowed = await context.Sessions
                .Where(s => s.Id == new EntityId<Session>(request.SessionId))
                .Select(s => s.IsLoyaltyPaymentAllowed)
                .FirstOrDefaultAsync(ct);

            if (!isLoyaltyAllowed)
                return Result.Failure<Guid>(new Error(
                    "Order.LoyaltyNotAllowed",
                    "Використання бонусних балів для цього сеансу недоступне."));

            var (points, _) = await loyaltyService.GetUserLoyaltyAsync(userId.Value, ct);

            if (points >= 75)
            {
                var maxAllowedDiscount = orderAmount * 0.5m;
                pointsToDeduct = (int)Math.Min(points, maxAllowedDiscount);
                amountToPay = orderAmount - pointsToDeduct; 
            }
        }

        PaymentResult paymentResult;
        try
        {
            paymentResult = await paymentService.ProcessPaymentAsync(amountToPay, "UAH", request.PaymentToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Payment crash for Order {OrderId}", orderId);
            paymentResult = PaymentResult.Failure("Payment system error");
        }
        
        if (paymentResult.IsSuccess)
        {
            if (pointsToDeduct > 0)
            {
                var idempotencyKey = Guid.NewGuid().ToString(); // Генеруємо ключ
                var deductResult = await loyaltyService.DeductPointsAsync(userId.Value, pointsToDeduct, orderId, idempotencyKey, ct);
                
                if (!deductResult.Success)
                {
                    logger.LogCritical("CRITICAL: Failed to deduct {Points} points for Order {OrderId}. Error: {Error}", pointsToDeduct, orderId, deductResult.Error);
                }
            }

            return await ConfirmOrderAsync(orderId, paymentResult.TransactionId!, userId.Value, request.SessionId, request.SeatIds, pointsToDeduct, ct);
        }
        else
        {
            return await FailOrderAsync(orderId, paymentResult.ErrorMessage!, request.SessionId, request.SeatIds, userId.Value, ct);
        }
    }
    
    private async Task<Result<Guid>> ConfirmOrderAsync(Guid orderId, string transactionId, Guid userId, Guid sessionId, List<Guid> seatIds, int pointsUsed, CancellationToken ct)
    {
        var order = await context.Orders.Include(o => o.Tickets).FirstOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), ct);
        if (order == null) return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found"));

        order.MarkAsPaid(transactionId);
        if (pointsUsed > 0)
        {
            order.ApplyLoyaltyDiscount(pointsUsed);
        }

        await context.SaveChangesAsync(ct);
        
        await publisher.Publish(new OrderPaidEvent(order), ct);

        foreach (var seatId in seatIds)
        {
           await seatLockingService.UnlockSeatAsync(sessionId, seatId, userId, ct);
        }
        
        return Result.Success(order.Id.Value);
    }

    private async Task<Result<Guid>> FailOrderAsync(Guid orderId, string reason, Guid sessionId, List<Guid> seatIds, Guid userId, CancellationToken ct)
    {
        var order = await context.Orders.FindAsync([new EntityId<Order>(orderId)], ct);
        if (order != null)
        {
            order.MarkAsFailed();
            await context.SaveChangesAsync(ct);
        }
        
        foreach(var seatId in seatIds) 
            await seatLockingService.UnlockSeatAsync(sessionId, seatId, userId, ct);

        return Result.Failure<Guid>(new Error("Payment.Failed", reason));
    }
}