using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
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
    private const string UnknownPaymentOutcomeCode = "payment_outcome_unknown";

    public Task<Result<Guid>> ProcessCheckoutAsync(
        Guid userId, Guid orderId, bool applyGoldUpgrade, bool useLoyaltyPoints,
        string paymentToken, CancellationToken ct)
        => new CheckoutExecutionStrategy(context).ExecuteAsync(() => ProcessCheckoutLockedAsync(
            userId, orderId, applyGoldUpgrade, useLoyaltyPoints, paymentToken, ct));

    public Task<Result<Guid>> ProcessCheckoutAsync(
        Guid userId,
        Guid sessionId,
        List<Guid> seatIds,
        Guid orderId,
        bool applyGoldUpgrade,
        bool useLoyaltyPoints,
        string paymentToken,
        CancellationToken ct)
        => ProcessCheckoutAsync(userId, orderId, applyGoldUpgrade, useLoyaltyPoints, paymentToken, ct);

    private async Task<Result<Guid>> ProcessCheckoutLockedAsync(
        Guid userId, Guid orderId,
        bool applyGoldUpgrade, bool useLoyaltyPoints, string paymentToken, CancellationToken ct)
    {
        await using CheckoutOrderLock checkoutLock = await CheckoutOrderLock.AcquireAsync(context, orderId, ct);
        if (!checkoutLock.IsAcquired)
            return Result.Failure<Guid>(new Error("Order.CheckoutInProgress", "Checkout or recovery is already in progress."));

        using CancellationTokenSource checkoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, checkoutLock.ConnectionLost);
        ct = checkoutCancellation.Token;
        Order? existing = await context.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId) && o.UserId == userId, ct);
        if (existing is null)
            return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found or unauthorized."));

        if (NeedsGoldRecovery(existing.GoldCheckoutState))
        {
            await RecoverGoldOrderAsync(orderId, ct);
            return Result.Failure<Guid>(new Error("Order.GoldRecoveryRequired", "Previous GOLD checkout failed; compensation is being reconciled."));
        }
        if (existing.GoldCheckoutState is GoldCheckoutState.Compensated or GoldCheckoutState.Rejected)
            return Result.Failure<Guid>(new Error("Order.InvalidState", "Cannot reuse an unsuccessful GOLD checkout."));

        try
        {
            return await ProcessCheckoutCoreAsync(userId, orderId,
                applyGoldUpgrade, useLoyaltyPoints, paymentToken, checkoutLock, ct);
        }
        finally
        {
            if (applyGoldUpgrade && !checkoutLock.ConnectionLost.IsCancellationRequested)
            {
                // Request cancellation must not cancel compensation. The journal survives
                // this bounded attempt, including an abrupt process termination.
                using CancellationTokenSource recoveryTimeout = CancellationTokenSource.CreateLinkedTokenSource(checkoutLock.ConnectionLost);
                recoveryTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await RecoverGoldOrderAsync(orderId, recoveryTimeout.Token);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "GOLD compensation remains pending for Order {OrderId}", orderId);
                }
            }
        }
    }

    private async Task<Result<Guid>> ProcessCheckoutCoreAsync(
        Guid userId, Guid orderId,
        bool applyGoldUpgrade, bool useLoyaltyPoints, string paymentToken, CheckoutOrderLock checkoutLock, CancellationToken ct)
    {
        var order = await context.Orders
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

        if (order.Status != OrderStatus.Pending)
        {
            return Result.Failure<Guid>(new Error("Order.InvalidState", "Only pending orders can be checked out."));
        }

        List<Ticket> tickets = await context.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.OrderId == order.Id)
            .ToListAsync(ct);

        if (tickets.Count == 0 || tickets.Any(t => t.SessionId != order.SessionId || t.TicketStatus != TicketStatus.Reserved))
            return Result.Failure<Guid>(new Error("Order.InvalidState", "Order tickets require reconciliation before checkout."));

        if (!await TryClaimPaymentAsync(order, ct))
            return Result.Failure<Guid>(new Error("Order.InvalidState", "Order is already being processed."));

        if (context.Database.ProviderName != "Microsoft.EntityFrameworkCore.InMemory")
        {
            // ExecuteUpdate does not update tracked instances. Start subsequent work from the claimed row.
            context.ClearChangeTracker();
            order = await context.Orders.SingleAsync(o => o.Id == new EntityId<Order>(orderId), ct);
            tickets = await context.Tickets.IgnoreQueryFilters().Where(t => t.OrderId == order.Id).ToListAsync(ct);
        }

        Guid trustedSessionId = order.SessionId.Value;
        List<Guid> trustedSeatIds = tickets.Select(t => t.SeatId.Value).ToList();

        if (applyGoldUpgrade && tickets.All(ticket => !ticket.IsGoldUpgraded))
        {
            var sessionWithPricing = await context.Sessions
                .Include(s => s.Pricing)
                .ThenInclude(p => p!.PricingItems)
                .FirstOrDefaultAsync(s => s.Id == new EntityId<Session>(trustedSessionId), ct);

            if (sessionWithPricing is null)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                await ResetUnchargedClaimAsync(order, ct);
                return Result.Failure<Guid>(new Error("Session.NotFound", "Session not found."));
            }

            var goldUpgradeQuoteResult = await goldUpgradePricingService.CalculateAsync(
                sessionWithPricing,
                tickets
                    .Where(t => !t.IsGoldUpgraded)
                    .Select(t => new GoldUpgradeTicketPrice(t.SeatId.Value, t.PriceSnapshot))
                    .ToList(),
                ct);

            if (goldUpgradeQuoteResult.IsFailure)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                await ResetUnchargedClaimAsync(order, ct);
                return Result.Failure<Guid>(goldUpgradeQuoteResult.Error);
            }

            var goldUpgradeQuote = goldUpgradeQuoteResult.Value;
            if (!goldUpgradeQuote.IsApplied)
            {
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                await ResetUnchargedClaimAsync(order, ct);
                return Result.Failure<Guid>(new Error(
                    "Order.NoEligibleTickets",
                    goldUpgradeQuote.Reason ?? "Select at least one premium or VIP seat to apply GOLD upgrade."));
            }


            Ticket? ticketToUpgrade = tickets
                .Where(t => !t.IsGoldUpgraded)
                .OrderByDescending(t => t.PriceSnapshot)
                .FirstOrDefault();

            if (ticketToUpgrade is null)
                throw new DomainException("No tickets found to upgrade.");

            if (ticketToUpgrade.PriceSnapshot <= goldUpgradeQuote.BasePrice)
                throw new DomainException("No eligible ticket found for gold upgrade (ticket price is already less than or equal to standard price).");

            decimal priceDifference = ticketToUpgrade.PriceSnapshot - goldUpgradeQuote.BasePrice;
            if (priceDifference > order.TotalAmount)
                throw new DomainException("Gold discount must be positive and cannot exceed the order total.");

            order.BeginGoldCheckout(ticketToUpgrade.Id.Value, ticketToUpgrade.PriceSnapshot);
            await context.SaveChangesAsync(ct);
            var goldUpgradeResult = await loyaltyService.UseGoldUpgradeAsync(userId, orderId, ct);
            if (!goldUpgradeResult.Success)
            {
                order.RejectGoldUse();
                await context.SaveChangesAsync(ct);
                await seatLockingService.UnlockSeatsAsync(trustedSessionId, trustedSeatIds, userId, ct);
                await ResetUnchargedClaimAsync(order, ct);
                return Result.Failure<Guid>(new Error("Order.GoldUpgradeFailed", $"Gold upgrade failed: {goldUpgradeResult.Error}"));
            }
            order.ConfirmGoldUse();
            order.ApplyGoldDiscount(priceDifference);
            ticketToUpgrade.ApplyGoldUpgrade(goldUpgradeQuote.BasePrice);
            await context.SaveChangesAsync(ct);
            logger.LogInformation("Applied Gold Upgrade to order {OrderId} for user {UserId}", orderId, userId);
        }

        decimal orderAmount = order.TotalAmount;

        var loyaltyDiscountResult = await ResolveLoyaltyDiscountAsync(userId, trustedSessionId, orderId, orderAmount, useLoyaltyPoints, ct);
        if (loyaltyDiscountResult.IsFailure)
        {
            await ResetUnchargedClaimAsync(order, ct);
            return Result.Failure<Guid>(loyaltyDiscountResult.Error);
        }

        var (pointsToDeduct, amountToPay) = loyaltyDiscountResult.Value;

        var paymentResult = await ProcessPaymentFlowAsync(order, amountToPay, paymentToken, pointsToDeduct, checkoutLock, ct);
        if (!paymentResult.IsSuccess)
        {
            if (paymentResult.FailureCode == UnknownPaymentOutcomeCode)
            {
                order.MarkPaymentOutcomeUnknown();
                await context.SaveChangesAsync(CancellationToken.None);
                return Result.Failure<Guid>(new Error("Payment.OutcomeUnknown", "Payment outcome requires reconciliation."));
            }

            return await FailOrderAsync(orderId, paymentResult.ErrorMessage!, ct);
        }

        try
        {
            return await ConfirmOrderAsync(orderId, paymentResult.TransactionId!, userId, pointsToDeduct, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Confirmation failed after payment for Order {OrderId}. Manual reconciliation required.", orderId);
            context.ClearChangeTracker();
            Order? persistedOrder = await context.Orders.SingleOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), CancellationToken.None);
            if (persistedOrder?.Status == OrderStatus.Paid && persistedOrder.PaymentTransactionId == paymentResult.TransactionId)
                return Result.Success(orderId);
            if (persistedOrder?.Status == OrderStatus.PaymentProcessing)
            {
                persistedOrder.MarkPaymentOutcomeUnknown();
                await context.SaveChangesAsync(CancellationToken.None);
            }
            return Result.Failure<Guid>(new Error("Payment.OutcomeUnknown", "Payment outcome requires reconciliation."));
        }
    }

    private static bool NeedsGoldRecovery(GoldCheckoutState state) => state is
        GoldCheckoutState.UsePending or GoldCheckoutState.Consumed or GoldCheckoutState.CompensationPending;

    public Task RecoverGoldCheckoutsAsync(CancellationToken ct)
        => new CheckoutExecutionStrategy(context).ExecuteAsync(() => RecoverGoldCheckoutsCoreAsync(ct));

    private async Task RecoverGoldCheckoutsCoreAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        List<Guid> orderIds = await context.Orders.AsNoTracking()
            .Where(o => (o.GoldCheckoutState == GoldCheckoutState.UsePending ||
                         o.GoldCheckoutState == GoldCheckoutState.Consumed ||
                         o.GoldCheckoutState == GoldCheckoutState.CompensationPending) &&
                        (o.GoldRecoveryAfter == null || o.GoldRecoveryAfter <= now))
            .OrderBy(o => o.GoldRecoveryAfter).Take(100).Select(o => o.Id.Value).ToListAsync(ct);
        foreach (Guid orderId in orderIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using CheckoutOrderLock checkoutLock = await CheckoutOrderLock.AcquireAsync(context, orderId, ct);
                if (checkoutLock.IsAcquired)
                {
                    using CancellationTokenSource recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, checkoutLock.ConnectionLost);
                    await RecoverGoldOrderAsync(orderId, recoveryCancellation.Token);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "GOLD recovery failed for Order {OrderId}; journal retained", orderId);
            }
        }
    }

    private async Task RecoverGoldOrderAsync(Guid orderId, CancellationToken ct)
    {
        // Reload the committed state: confirmation may have mutated tracked entities
        // before a failed SaveChanges. Never persist those mutations during cleanup.
        Order? order = await context.Orders.AsNoTracking()
            .SingleOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), ct);
        if (order is null || !NeedsGoldRecovery(order.GoldCheckoutState) || order.Status == OrderStatus.Paid)
            return;

        context.ClearChangeTracker();
        context.Orders.Attach(order);
        order.RequestGoldCompensation();
        await context.SaveChangesAsync(ct);
        bool reservationExists = order.GoldUseConfirmed;
        if (!reservationExists)
        {
            // An interrupted Use may have committed. Replaying its identity settles
            // that ambiguity; it must never turn into a new order or monthly claim.
            var replay = await loyaltyService.UseGoldUpgradeAsync(order.UserId, orderId, ct);
            reservationExists = replay.Success;
            if (reservationExists)
            {
                order.ConfirmGoldUse();
                order.RequestGoldCompensation();
                await context.SaveChangesAsync(ct);
            }
        }
        if (reservationExists)
        {
            var rollback = await loyaltyService.RollbackGoldUpgradeAsync(order.UserId, orderId, ct);
            if (!rollback.Success)
            {
                logger.LogError("GOLD rollback pending for Order {OrderId}: {Error}", orderId, rollback.Error);
                return;
            }
        }

        Guid ticketId = order.GoldTicketId ?? throw new InvalidOperationException("GOLD journal has no ticket identity.");
        List<Ticket> tickets = await context.Tickets.IgnoreQueryFilters()
            .Where(t => t.OrderId == order.Id).ToListAsync(ct);
        Ticket upgraded = tickets.Single(t => t.Id == new EntityId<Ticket>(ticketId));
        upgraded.RestoreGoldUpgrade(order.GoldOriginalTicketPrice);
        order.CompleteGoldCompensation();
        if (order.Status == OrderStatus.Failed)
        {
            foreach (Ticket ticket in tickets.Where(t => t.TicketStatus == TicketStatus.Reserved))
                ticket.ReleaseReservation(order);
        }
        // Ticket, order prices and completion marker commit atomically.
        await context.SaveChangesAsync(ct);
    }

    private async Task<bool> TryClaimPaymentAsync(Order order, CancellationToken ct)
    {
        if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            order.StartPayment();
            await context.SaveChangesAsync(ct);
            return true;
        }

        return await context.Orders
            .Where(candidate => candidate.Id == order.Id && candidate.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, OrderStatus.PaymentProcessing), ct) == 1;
    }

    private async Task ResetUnchargedClaimAsync(Order order, CancellationToken ct)
    {
        if (context.Database.ProviderName == "Microsoft.EntityFrameworkCore.InMemory")
        {
            order.ResetPaymentBeforeCharge();
            await context.SaveChangesAsync(ct);
            return;
        }

        await context.Orders
            .Where(candidate => candidate.Id == order.Id && candidate.Status == OrderStatus.PaymentProcessing)
            .ExecuteUpdateAsync(setters => setters.SetProperty(candidate => candidate.Status, OrderStatus.Pending), ct);
        context.ClearChangeTracker();
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
        Order order, decimal amountToPay, string paymentToken, int pointsToDeduct, CheckoutOrderLock checkoutLock, CancellationToken ct)
    {
        Guid userId = order.UserId;
        Guid orderId = order.Id.Value;
        if (pointsToDeduct > 0)
        {
            var deductResult = await DeductLoyaltyPointsAsync(userId, orderId, pointsToDeduct, ct);
            if (!deductResult.IsSuccess)
            {
                return deductResult;
            }
        }

        if (order.GoldCheckoutState == GoldCheckoutState.Consumed)
        {
            // A crash during charge must not free potentially paid seats.
            order.BeginGoldPayment();
            await context.SaveChangesAsync(ct);
        }
        await checkoutLock.EnsureHeldAsync(ct);
        PaymentResult paymentResult;
        try
        {
            paymentResult = await paymentService.ProcessPaymentAsync(amountToPay, "UAH", paymentToken, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Payment system crash for Order {OrderId}", orderId);
            paymentResult = PaymentResult.Failure("Payment outcome requires reconciliation", UnknownPaymentOutcomeCode);
        }

        if (!paymentResult.IsSuccess && paymentResult.FailureCode != UnknownPaymentOutcomeCode)
        {
            if (order.GoldPaymentOutcomeUnknown)
            {
                order.ResolveGoldPayment();
                await context.SaveChangesAsync(ct);
            }
            if (pointsToDeduct > 0)
            {
                await CompensateLoyaltyPointsAsync(userId, orderId, pointsToDeduct, ct);
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
        Guid orderId, string transactionId, Guid userId, int pointsUsed, CancellationToken ct)
    {
        var order = await context.Orders
            .FirstOrDefaultAsync(o => o.Id == new EntityId<Order>(orderId), ct);

        if (order == null)
            return Result.Failure<Guid>(new Error("Order.NotFound", "Order not found"));

        if (pointsUsed > 0)
            order.ApplyLoyaltyDiscount(pointsUsed);

        order.MarkAsPaid(transactionId);
        order.CompleteGoldCheckout();

        List<Ticket> tickets = await context.Tickets.IgnoreQueryFilters()
            .Where(ticket => ticket.OrderId == order.Id).ToListAsync(ct);
        foreach (Ticket ticket in tickets)
            ticket.ConfirmPurchase(order);

        int ticketCount = tickets.Count;

        await publishEndpoint.Publish(new TicketPurchasedEvent(
            userId,
            orderId,
            order.TotalAmount,
            ticketCount,
            DateTime.UtcNow
        ), ct);
        await context.SaveChangesAsync(ct);

        return Result.Success(order.Id.Value);
    }

    private async Task<Result<Guid>> FailOrderAsync(Guid orderId, string reason, CancellationToken ct)
    {
        var order = await context.Orders.FindAsync([new EntityId<Order>(orderId)], ct);
        if (order != null)
        {
            List<Ticket> tickets = await context.Tickets.IgnoreQueryFilters()
                .Where(ticket => ticket.OrderId == order.Id && ticket.TicketStatus == TicketStatus.Reserved)
                .ToListAsync(ct);
            order.MarkAsFailed();
            foreach (Ticket ticket in tickets)
                ticket.ReleaseReservation(order);
            await context.SaveChangesAsync(ct);
        }

        return Result.Failure<Guid>(new Error("Payment.Failed", reason));
    }
}
