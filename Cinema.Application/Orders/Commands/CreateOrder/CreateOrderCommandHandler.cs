using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
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
    IPriceCalculator priceCalculator,
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

        if (request.ApplyGoldUpgrade)
        {
            var orderToUpgrade = await context.Orders
                .Include(o => o.Tickets)
                .FirstOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), ct);

            if (orderToUpgrade == null)
            {
                return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found during gold upgrade processing."));
            }

            var sessionWithPricing = await context.Sessions
                .Include(s => s.Pricing)
                .ThenInclude(p => p!.PricingItems)
                .FirstOrDefaultAsync(s => s.Id == new EntityId<Session>(request.SessionId), ct);

            var standardSeatType = await context.SeatTypes
                .FirstOrDefaultAsync(st => st.Name.ToUpper() == "STANDARD", ct);

            if (sessionWithPricing?.Pricing == null || standardSeatType == null)
            {
                return Result.Failure<Guid>(new Error("Order.StandardPriceNotFound", "Could not determine standard seat price for the upgrade."));
            }

            decimal standardPrice = priceCalculator.CalculatePrice(sessionWithPricing.Pricing, standardSeatType.Id, sessionWithPricing.StartTime);

            if (!orderToUpgrade.Tickets.Any(t => t.PriceSnapshot > standardPrice && !t.IsGoldUpgraded))
            {
                await seatLockingService.UnlockSeatsAsync(request.SessionId, request.SeatIds, userId.Value, ct);
                return Result.Failure<Guid>(new Error("Order.NoEligibleTickets", "No tickets eligible for gold upgrade."));
            }

            var goldUpgradeResult = await loyaltyService.UseGoldUpgradeAsync(userId.Value, orderId, ct);
            if (!goldUpgradeResult.Success)
            {
                await seatLockingService.UnlockSeatsAsync(request.SessionId, request.SeatIds, userId.Value, ct);
                return Result.Failure<Guid>(new Error("Order.GoldUpgradeFailed", $"Gold upgrade failed: {goldUpgradeResult.Error}"));
            }

            orderToUpgrade.ApplyGoldSeatUpgrade(standardPrice);
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Applied Gold Upgrade to order {OrderId} for user {UserId}", orderId, userId.Value);
        }

        var orderAmount = await context.Orders
            .Where(o => o.Id == new EntityId<Order>(orderId))
            .Select(o => o.TotalAmount)
            .FirstOrDefaultAsync(ct);

        var loyaltyDiscountResult = await ResolveLoyaltyDiscountAsync(userId.Value, request.SessionId, orderId, orderAmount, request.UseLoyaltyPoints, ct);
        if (loyaltyDiscountResult.IsFailure)
        {
            return Result.Failure<Guid>(loyaltyDiscountResult.Error);
        }

        var (pointsToDeduct, amountToPay) = loyaltyDiscountResult.Value;

        var paymentResult = await ProcessPaymentFlowAsync(userId.Value, orderId, amountToPay, request.PaymentToken, pointsToDeduct, request.ApplyGoldUpgrade, ct);
        if (!paymentResult.IsSuccess)
        {
            return await FailOrderAsync(orderId, paymentResult.ErrorMessage!, request.SessionId, request.SeatIds, userId.Value, ct);
        }

        return await ConfirmOrderAsync(orderId, paymentResult.TransactionId!, userId.Value, request.SessionId, request.SeatIds, pointsToDeduct, ct);
    }

    private async Task<Result<(int PointsToDeduct, decimal AmountToPay)>> ResolveLoyaltyDiscountAsync(
        Guid userId, Guid sessionId, Guid orderId, decimal orderAmount, bool useLoyaltyPoints, CancellationToken ct)
    {
        if (!useLoyaltyPoints)
        {
            return Result.Success((0, orderAmount));
        }

        var isLoyaltyAllowed = await context.Sessions
            .Where(s => s.Id == new EntityId<Session>(sessionId))
            .Select(s => s.IsLoyaltyPaymentAllowed)
            .FirstOrDefaultAsync(ct);

        if (!isLoyaltyAllowed)
        {
            return Result.Failure<(int, decimal)>(new Error(
                "Order.LoyaltyNotAllowed",
                "Використання бонусних балів для цього сеансу недоступне."));
        }

        try
        {
            var (isAllowed, points, amount) = await loyaltyService.CalculateDiscountAsync(userId, orderAmount, ct);
            return isAllowed 
                ? Result.Success((points, amount)) 
                : Result.Success((0, orderAmount));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loyalty service unavailable while calculating discount for Order {OrderId}", orderId);
            return Result.Failure<(int, decimal)>(new Error(
                "Loyalty.Unavailable",
                "Loyalty service is currently unavailable. Please try again without loyalty points or retry later."));
        }
    }

    private async Task<PaymentResult> ProcessPaymentFlowAsync(
        Guid userId, Guid orderId, decimal amountToPay, string paymentToken, int pointsToDeduct, bool applyGoldUpgrade, CancellationToken ct)
    {
        if (pointsToDeduct > 0)
        {
            var deductResult = await DeductLoyaltyPointsAsync(userId, orderId, pointsToDeduct, ct);
            if (!deductResult.IsSuccess)
            {
                return deductResult;
            }
        }

        PaymentResult paymentResult;
        try
        {
            paymentResult = await paymentService.ProcessPaymentAsync(amountToPay, "UAH", paymentToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Payment system crash for Order {OrderId}", orderId);
            paymentResult = PaymentResult.Failure("Payment system error");
        }

        if (!paymentResult.IsSuccess)
        {
            if (pointsToDeduct > 0)
            {
                await CompensateLoyaltyPointsAsync(userId, orderId, pointsToDeduct, ct);
            }

            if (applyGoldUpgrade)
            {
                var rollbackResult = await loyaltyService.RollbackGoldUpgradeAsync(userId, orderId, ct);
                if (!rollbackResult.Success)
                {
                    logger.LogCritical("SAGA COMPENSATION FAILED: Could not rollback Gold Upgrade for user {UserId}, Order {OrderId}. Error: {Error}", userId, orderId, rollbackResult.Error);
                }
                else
                {
                    logger.LogWarning("Payment failed for Order {OrderId}. Successfully rolled back Gold Upgrade for user {UserId}.", orderId, userId);
                }
            }
        }

        return paymentResult;
    }

    private async Task<PaymentResult> DeductLoyaltyPointsAsync(Guid userId, Guid orderId, int pointsToDeduct, CancellationToken ct)
    {
        string deductKey = DeterministicGuid.Create($"deduct-{orderId}").ToString();
        var deductResult = await loyaltyService.DeductPointsAsync(userId, pointsToDeduct, orderId, deductKey, ct);

        if (!deductResult.Success)
        {
            logger.LogError(
                "Failed to deduct {Points} points for Order {OrderId}. Reason: {Error}",
                pointsToDeduct, orderId, deductResult.Error);

            return PaymentResult.Failure($"Could not deduct loyalty points: {deductResult.Error}");
        }

        logger.LogInformation(
            "Deducted {Points} loyalty points for user {UserId}, Order {OrderId}. Balance after: {Balance}",
            pointsToDeduct, userId, orderId, deductResult.BalanceAfter);

        return PaymentResult.Success(string.Empty);
    }

    private async Task CompensateLoyaltyPointsAsync(Guid userId, Guid orderId, int pointsToDeduct, CancellationToken ct)
    {
        string refundKey = DeterministicGuid.Create($"refund-{orderId}").ToString();
        var refundResult = await loyaltyService.RefundPointsAsync(userId, pointsToDeduct, orderId, refundKey, ct);

        if (!refundResult.Success)
        {
            logger.LogCritical(
                "SAGA COMPENSATION FAILED: Could not refund {Points} points for user {UserId}, Order {OrderId}. " +
                "Manual reconciliation required. Refund error: {Error}",
                pointsToDeduct, userId, orderId, refundResult.Error);
        }
        else
        {
            logger.LogWarning(
                "Payment failed for Order {OrderId}. Successfully refunded {Points} points to user {UserId}.",
                orderId, pointsToDeduct, userId);
        }
    }

    private async Task<Result<Guid>> ConfirmOrderAsync(
        Guid orderId, string transactionId, Guid userId,
        Guid sessionId, List<Guid> seatIds, int pointsUsed, CancellationToken ct)
    {
        var order = await context.Orders
            .Include(o => o.Tickets)
            .FirstOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), ct);

        if (order == null)
            return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found"));

        if (pointsUsed > 0)
            order.ApplyLoyaltyDiscount(pointsUsed);

        order.MarkAsPaid(transactionId);

        await context.SaveChangesAsync(ct);

        return Result.Success(order.Id.Value);
    }

    private async Task<Result<Guid>> FailOrderAsync(
        Guid orderId, string reason,
        Guid sessionId, List<Guid> seatIds, Guid userId, CancellationToken ct)
    {
        var order = await context.Orders.FindAsync([new EntityId<Order>(orderId)], ct);
        if (order != null)
        {
            order.MarkAsFailed();
            await context.SaveChangesAsync(ct);
        }

        return Result.Failure<Guid>(new Error("Payment.Failed", reason));
    }
}