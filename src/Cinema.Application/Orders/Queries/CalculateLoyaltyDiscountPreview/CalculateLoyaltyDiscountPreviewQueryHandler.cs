using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Orders.Queries.CalculateLoyaltyDiscountPreview;

public class CalculateLoyaltyDiscountPreviewQueryHandler(
    IApplicationDbContext context,
    ICurrentUserService currentUser,
    ILoyaltyService loyaltyService,
    IPriceCalculator priceCalculator,
    ILogger<CalculateLoyaltyDiscountPreviewQueryHandler> logger)
    : IRequestHandler<CalculateLoyaltyDiscountPreviewQuery, Result<LoyaltyDiscountPreviewDto>>
{
    public async Task<Result<LoyaltyDiscountPreviewDto>> Handle(
        CalculateLoyaltyDiscountPreviewQuery request,
        CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null)
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
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
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Session.NotFound", "Session not found"));
        }

        if (session.StartTime <= DateTime.UtcNow)
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Session.Started", "Cannot calculate discount for a started session."));
        }

        if (session.Pricing is null)
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Pricing.NotFound", "Pricing not found"));
        }

        var targetSeatIds = seatIds.Select(id => new EntityId<Seat>(id)).ToList();
        var seats = await context.Seats
            .AsNoTracking()
            .Where(s => targetSeatIds.Contains(s.Id))
            .ToListAsync(ct);

        if (seats.Count != seatIds.Count)
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Order.SeatsNotFound", "Mismatch in seat availability."));
        }

        if (seats.Any(s => s.HallId != session.HallId))
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Order.SeatsWrongHall", "Selected seats belong to a different hall."));
        }

        if (seats.Any(s => s.Status != SeatStatus.Active))
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Order.SeatsNotActive", "Seats not active."));
        }

        var areSeatsSold = await context.Tickets
            .AsNoTracking()
            .AnyAsync(t => targetSeatIds.Contains(t.SeatId)
                           && t.SessionId == sessionId
                           && (t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used), ct);

        if (areSeatsSold)
        {
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Order.SeatsSold", "Seats already sold."));
        }

        var orderAmount = seats.Sum(seat =>
            priceCalculator.CalculatePrice(session.Pricing, seat.SeatTypeId, session.StartTime));

        if (!session.IsLoyaltyPaymentAllowed)
        {
            return Result.Success(new LoyaltyDiscountPreviewDto(
                orderAmount,
                false,
                0,
                orderAmount,
                "Loyalty points cannot be used for this session."));
        }

        try
        {
            var (isAllowed, pointsToDeduct, amountToPay) =
                await loyaltyService.CalculateDiscountAsync(userId.Value, orderAmount, ct);

            return Result.Success(new LoyaltyDiscountPreviewDto(
                orderAmount,
                isAllowed,
                pointsToDeduct,
                amountToPay,
                isAllowed ? null : "Not enough loyalty points to apply a discount."));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Loyalty service unavailable while previewing discount for User {UserId}", userId);
            return Result.Failure<LoyaltyDiscountPreviewDto>(
                new Error("Loyalty.Unavailable", "Loyalty service is currently unavailable."));
        }
    }
}
