using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.Services;

public class OrderCheckoutOrchestrator(
    IApplicationDbContext context,
    ILoyaltyService loyaltyService,
    IPaymentService paymentService,
    ISeatLockingService seatLockingService,
    IGoldUpgradePricingService goldUpgradePricingService,
    IPublishEndpoint publishEndpoint,
    ILogger<OrderCheckoutOrchestrator> logger) : IOrderCheckoutOrchestrator
{
    public async Task<Result<Guid>> ProcessCheckoutAsync(
        Guid userId,
        Guid sessionId,
        List<Guid> seatIds,
        Guid orderId,
        bool applyGoldUpgrade,
        bool useLoyaltyPoints,
        string paymentToken,
        CancellationToken ct)
    {
        var order = await context.Orders
            .Include(o => o.Tickets)
            .FirstOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId) && o.UserId == userId, ct);

        if (order == null)
        {
            return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found or unauthorized."));
        }

        if (order.Status == OrderStatus.Paid)
        {
            logger.LogInformation("Idempotent request: Order {OrderId} is already paid.", orderId);
            return Result.Success(order.Id.Value);
        }

        if (order.Status == OrderStatus.Failed)
        {
            return Result.Failure<Guid>(new Error("Order.InvalidState", "Cannot checkout a failed order."));
        }

        Guid trustedSessionId = order.SessionId.Value;
        List<Guid> trustedSeatIds = order.Tickets.Select(t => t.SeatId.Value).ToList();

        if (applyGoldUpgrade)
        {
            var sessionWithPricing = await context.Sessions
                .Include(s => s.Pricing)
                .ThenInclude(p => p!.PricingItems)
                .FirstOrDefaultAsync(s => s.Id == new EntityId<Session>(trustedSessionId), ct);

            if (sessionWithPricing is null)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                return Result.Failure<Guid>(new Error("Session.NotFound", "Session not found."));
            }

            var goldUpgradeQuoteResult = await goldUpgradePricingService.CalculateAsync(
                sessionWithPricing,
                order.Tickets
                    .Where(t => !t.IsGoldUpgraded)
                    .Select(t => new GoldUpgradeTicketPrice(t.SeatId.Value, t.PriceSnapshot))
                    .ToList(),
                ct);

            if (goldUpgradeQuoteResult.IsFailure)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                return Result.Failure<Guid>(goldUpgradeQuoteResult.Error);
            }

            var goldUpgradeQuote = goldUpgradeQuoteResult.Value;
            if (!goldUpgradeQuote.IsApplied)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                return Result.Failure<Guid>(new Error(
                    "Order.NoEligibleTickets",
                    goldUpgradeQuote.Reason ?? "Select at least one premium or VIP seat to apply GOLD upgrade."));
            }

            var goldUpgradeResult = await loyaltyService.UseGoldUpgradeAsync(userId, orderId, ct);
            if (!goldUpgradeResult.Success)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                return Result.Failure<Guid>(new Error("Order.GoldUpgradeFailed", $"Gold upgrade failed: {goldUpgradeResult.Error}"));
            }

            order.ApplyGoldSeatUpgrade(goldUpgradeQuote.BasePrice);
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Applied Gold Upgrade to order {OrderId} for user {UserId}", orderId, userId);
        }

        decimal orderAmount = order.TotalAmount;

        var loyaltyDiscountResult = await ResolveLoyaltyDiscountAsync(userId, trustedSessionId, orderId, orderAmount, useLoyaltyPoints, ct);
        if (loyaltyDiscountResult.IsFailure)
        {
            return Result.Failure<Guid>(loyaltyDiscountResult.Error);
        }

        var (pointsToDeduct, amountToPay) = loyaltyDiscountResult.Value;

        var paymentResult = await ProcessPaymentFlowAsync(userId, orderId, amountToPay, paymentToken, pointsToDeduct, applyGoldUpgrade, ct);
        if (!paymentResult.IsSuccess)
        {
            return await FailOrderAsync(orderId, paymentResult.ErrorMessage!, trustedSessionId, trustedSeatIds, userId, ct);
        }

        try
        {
            return await ConfirmOrderAsync(orderId, paymentResult.TransactionId!, userId, trustedSessionId, trustedSeatIds, pointsToDeduct, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "FATAL: DB Update failed after successful payment for Order {OrderId}. Initiating emergency refunds.", orderId);
            
            await paymentService.RefundPaymentAsync(paymentResult.TransactionId!, ct);
            
            if (pointsToDeduct > 0)
            {
                await CompensateLoyaltyPointsAsync(userId, orderId, pointsToDeduct, ct);
            }
                
            return Result.Failure<Guid>(new Error("Order.ConfirmationFailed", "System error occurred after payment. Payment has been refunded."));
        }
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

        await publishEndpoint.Publish(new TicketPurchasedEvent(
            userId,
            orderId,
            order.TotalAmount,
            order.Tickets.Count,
            DateTime.UtcNow
        ), ct);

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
