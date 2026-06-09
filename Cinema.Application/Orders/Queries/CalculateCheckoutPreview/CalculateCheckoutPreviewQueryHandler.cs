using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.Queries.CalculateCheckoutPreview;

public class CalculateCheckoutPreviewQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser,
    ILoyaltyService loyaltyService,
    IPriceCalculator priceCalculator,
    IGoldUpgradePricingService goldUpgradePricingService,
    ISeatTypeProvider seatTypeProvider,
    ILogger<CalculateCheckoutPreviewQueryHandler> logger)
    : IRequestHandler<CalculateCheckoutPreviewQuery, Result<CheckoutPreviewDto>>
{
    private const string Currency = "UAH";

    public async Task<Result<CheckoutPreviewDto>> Handle(
        CalculateCheckoutPreviewQuery request,
        CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null)
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Auth.Required", "User not authenticated"));
        }

        var seatIds = request.SeatIds.Distinct().ToList();
        var sessionId = new EntityId<Session>(request.SessionId);

        var session = await context.Sessions
            .Include(s => s.Pricing)
            .ThenInclude(p => p!.PricingItems)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session is null)
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Session.NotFound", "Session not found"));
        }

        if (session.StartTime <= DateTime.UtcNow)
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Session.Started", "Cannot calculate checkout preview for a started session."));
        }

        if (session.Pricing is null)
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Pricing.NotFound", "Pricing not found"));
        }

        var targetSeatIds = seatIds.Select(id => new EntityId<Seat>(id)).ToList();
        var seats = await context.Seats
            .AsNoTracking()
            .Where(s => targetSeatIds.Contains(s.Id))
            .ToListAsync(ct);

        if (seats.Count != seatIds.Count)
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Order.SeatsNotFound", "Mismatch in seat availability."));
        }

        if (seats.Any(s => s.HallId != session.HallId))
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Order.SeatsWrongHall", "Selected seats belong to a different hall."));
        }

        if (seats.Any(s => s.Status != SeatStatus.Active))
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Order.SeatsNotActive", "Seats not active."));
        }

        var areSeatsSold = await context.Tickets
            .AsNoTracking()
            .AnyAsync(t => targetSeatIds.Contains(t.SeatId)
                           && t.SessionId == sessionId
                           && (t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);

        if (areSeatsSold)
        {
            return Result.Failure<CheckoutPreviewDto>(
                new Error("Order.SeatsSold", "Seats already sold."));
        }

        var selectedTickets = BuildSelectedTickets(seatIds, seats, session);
        var originalAmount = selectedTickets.Sum(t => t.OriginalPrice);
        var ticketFinalPrices = selectedTickets.ToDictionary(t => t.SeatId, t => t.OriginalPrice);
        var warnings = new List<string>();

        var goldUpgrade = await BuildGoldUpgradePreviewAsync(
            request.ApplyGoldUpgrade,
            userId.Value,
            session,
            selectedTickets,
            ticketFinalPrices,
            warnings,
            ct);

        if (goldUpgrade.IsFailure)
        {
            return Result.Failure<CheckoutPreviewDto>(goldUpgrade.Error);
        }

        var amountAfterGoldUpgrade = ticketFinalPrices.Values.Sum();

        var loyaltyPoints = await BuildLoyaltyPointsPreviewAsync(
            request.UseLoyaltyPoints,
            userId.Value,
            session.IsLoyaltyPaymentAllowed,
            amountAfterGoldUpgrade,
            warnings,
            ct);

        if (loyaltyPoints.IsFailure)
        {
            return Result.Failure<CheckoutPreviewDto>(loyaltyPoints.Error);
        }

        var finalAmountToPay = loyaltyPoints.Value.Applied
            ? loyaltyPoints.Value.AmountToPay
            : amountAfterGoldUpgrade;

        var canCheckout = CanCheckoutWithSelectedOptions(
            request.ApplyGoldUpgrade,
            goldUpgrade.Value,
            request.UseLoyaltyPoints,
            session.IsLoyaltyPaymentAllowed);

        var ticketDtos = selectedTickets
            .Select(t => new CheckoutTicketPreviewDto(
                t.SeatId,
                t.SeatType,
                t.OriginalPrice,
                ticketFinalPrices[t.SeatId],
                t.OriginalPrice - ticketFinalPrices[t.SeatId],
                ticketFinalPrices[t.SeatId] < t.OriginalPrice))
            .ToList();

        return Result.Success(new CheckoutPreviewDto(
            Currency,
            canCheckout,
            originalAmount,
            amountAfterGoldUpgrade,
            finalAmountToPay,
            originalAmount - finalAmountToPay,
            goldUpgrade.Value,
            loyaltyPoints.Value,
            ticketDtos,
            warnings));
    }

    private List<SelectedTicketPreview> BuildSelectedTickets(
        IReadOnlyCollection<Guid> orderedSeatIds,
        IReadOnlyCollection<Seat> seats,
        Session session)
    {
        var seatsById = seats.ToDictionary(s => s.Id.Value);
        var priceCache = new Dictionary<EntityId<SeatType>, decimal>();
        var result = new List<SelectedTicketPreview>(orderedSeatIds.Count);

        foreach (var seatId in orderedSeatIds)
        {
            var seat = seatsById[seatId];
            if (!priceCache.TryGetValue(seat.SeatTypeId, out var price))
            {
                price = priceCalculator.CalculatePrice(session.Pricing!, seat.SeatTypeId, session.StartTime);
                priceCache[seat.SeatTypeId] = price;
            }

            result.Add(new SelectedTicketPreview(
                seat.Id.Value,
                seatTypeProvider.GetName(seat.SeatTypeId),
                price));
        }

        return result;
    }

    private async Task<Result<CheckoutGoldUpgradePreviewDto>> BuildGoldUpgradePreviewAsync(
        bool requested,
        Guid userId,
        Session session,
        IReadOnlyCollection<SelectedTicketPreview> selectedTickets,
        Dictionary<Guid, decimal> ticketFinalPrices,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!requested)
        {
            return Result.Success(new CheckoutGoldUpgradePreviewDto(false, false, false, 0m, null, null, null, null));
        }

        LoyaltyProfileDto profile;
        try
        {
            profile = await loyaltyService.GetUserLoyaltyProfileAsync(userId, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loyalty service unavailable while previewing GOLD upgrade for User {UserId}", userId);
            return Result.Failure<CheckoutGoldUpgradePreviewDto>(
                new Error("Loyalty.Unavailable", "Loyalty service is currently unavailable."));
        }

        if (!profile.GoldUpgradeAvailable)
        {
            const string reason = "GOLD upgrade is not available for this user.";
            warnings.Add(reason);
            return Result.Success(new CheckoutGoldUpgradePreviewDto(true, false, false, 0m, null, null, null, reason));
        }

        var quoteResult = await goldUpgradePricingService.CalculateAsync(
            session,
            selectedTickets
                .Select(t => new GoldUpgradeTicketPrice(t.SeatId, t.OriginalPrice))
                .ToList(),
            ct);

        if (quoteResult.IsFailure)
        {
            return Result.Failure<CheckoutGoldUpgradePreviewDto>(quoteResult.Error);
        }

        var quote = quoteResult.Value;
        if (!quote.IsApplied || quote.SeatId is null || quote.PriceAfter is null)
        {
            var reason = quote.Reason ?? "No selected ticket can use GOLD upgrade.";
            warnings.Add(reason);
            return Result.Success(new CheckoutGoldUpgradePreviewDto(true, false, false, 0m, null, null, null, reason));
        }

        ticketFinalPrices[quote.SeatId.Value] = quote.PriceAfter.Value;

        return Result.Success(new CheckoutGoldUpgradePreviewDto(
            true,
            true,
            true,
            quote.DiscountAmount,
            quote.SeatId,
            quote.PriceBefore,
            quote.PriceAfter,
            null));
    }

    private async Task<Result<CheckoutLoyaltyPointsPreviewDto>> BuildLoyaltyPointsPreviewAsync(
        bool requested,
        Guid userId,
        bool isLoyaltyPaymentAllowed,
        decimal amountAfterGoldUpgrade,
        List<string> warnings,
        CancellationToken ct)
    {
        if (!requested)
        {
            return Result.Success(new CheckoutLoyaltyPointsPreviewDto(
                false,
                false,
                false,
                0,
                0m,
                amountAfterGoldUpgrade,
                null));
        }

        if (!isLoyaltyPaymentAllowed)
        {
            const string reason = "Loyalty points cannot be used for this session.";
            warnings.Add(reason);
            return Result.Success(new CheckoutLoyaltyPointsPreviewDto(
                true,
                false,
                false,
                0,
                0m,
                amountAfterGoldUpgrade,
                reason));
        }

        try
        {
            var (isAllowed, pointsToDeduct, amountToPay) =
                await loyaltyService.CalculateDiscountAsync(userId, amountAfterGoldUpgrade, ct);

            if (!isAllowed || pointsToDeduct <= 0)
            {
                const string reason = "Not enough loyalty points to apply a discount.";
                warnings.Add(reason);
                return Result.Success(new CheckoutLoyaltyPointsPreviewDto(
                    true,
                    false,
                    false,
                    0,
                    0m,
                    amountAfterGoldUpgrade,
                    reason));
            }

            return Result.Success(new CheckoutLoyaltyPointsPreviewDto(
                true,
                true,
                true,
                pointsToDeduct,
                pointsToDeduct,
                amountToPay,
                null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loyalty service unavailable while previewing discount for User {UserId}", userId);
            return Result.Failure<CheckoutLoyaltyPointsPreviewDto>(
                new Error("Loyalty.Unavailable", "Loyalty service is currently unavailable."));
        }
    }

    private static bool CanCheckoutWithSelectedOptions(
        bool goldRequested,
        CheckoutGoldUpgradePreviewDto goldUpgrade,
        bool loyaltyPointsRequested,
        bool isLoyaltyPaymentAllowed)
    {
        if (goldRequested && !goldUpgrade.Applied)
        {
            return false;
        }

        if (loyaltyPointsRequested && !isLoyaltyPaymentAllowed)
        {
            return false;
        }

        return true;
    }

    private sealed record SelectedTicketPreview(
        Guid SeatId,
        string SeatType,
        decimal OriginalPrice);
}