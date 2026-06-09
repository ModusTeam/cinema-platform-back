using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Orders.Services;

public class GoldUpgradePricingService(
    IApplicationDbContext context,
    IPriceCalculator priceCalculator) : IGoldUpgradePricingService
{
    private const string StandardSeatTypeName = "STANDARD";
    private const string NoEligibleTicketReason = "Select at least one premium or VIP seat to apply GOLD upgrade.";

    public async Task<Result<GoldUpgradePricingQuote>> CalculateAsync(
        Session session,
        IReadOnlyCollection<GoldUpgradeTicketPrice> tickets,
        CancellationToken ct = default)
    {
        var standardSeatType = await context.SeatTypes
            .AsNoTracking()
            .FirstOrDefaultAsync(st => st.Name.ToUpper() == StandardSeatTypeName, ct);

        var basePriceResult = ResolveBasePrice(session, standardSeatType);
        if (basePriceResult.IsFailure)
        {
            return Result.Failure<GoldUpgradePricingQuote>(basePriceResult.Error);
        }

        var basePrice = basePriceResult.Value;
        var ticketToUpgrade = tickets
            .Where(t => t.Price > basePrice)
            .OrderByDescending(t => t.Price)
            .FirstOrDefault();

        if (ticketToUpgrade is null)
        {
            return Result.Success(new GoldUpgradePricingQuote(
                false,
                basePrice,
                0m,
                null,
                null,
                null,
                NoEligibleTicketReason));
        }

        return Result.Success(new GoldUpgradePricingQuote(
            true,
            basePrice,
            ticketToUpgrade.Price - basePrice,
            ticketToUpgrade.SeatId,
            ticketToUpgrade.Price,
            basePrice,
            null));
    }

    private Result<decimal> ResolveBasePrice(Session session, SeatType? standardSeatType)
    {
        if (session.Pricing is null)
        {
            return Result.Failure<decimal>(new Error(
                "Order.StandardPriceNotFound",
                "Could not determine base seat price for the GOLD upgrade."));
        }

        if (standardSeatType is not null)
        {
            var standardPrice = TryCalculatePrice(session.Pricing, standardSeatType.Id, session.StartTime);
            if (standardPrice.HasValue)
            {
                return Result.Success(standardPrice.Value);
            }
        }

        var fallbackPrices = session.Pricing.PricingItems?
            .Select(item => TryCalculatePrice(session.Pricing, item.SeatTypeId, session.StartTime))
            .Where(price => price.HasValue)
            .Select(price => price!.Value)
            .ToList() ?? [];

        if (fallbackPrices.Count == 0)
        {
            return Result.Failure<decimal>(new Error(
                "Order.StandardPriceNotFound",
                "Could not determine base seat price for the GOLD upgrade."));
        }

        var fallbackPrice = fallbackPrices.Min();
        if (fallbackPrice <= 0)
        {
            return Result.Failure<decimal>(new Error(
                "Order.StandardPriceNotFound",
                "Could not determine base seat price for the GOLD upgrade."));
        }

        return Result.Success(fallbackPrice);
    }

    private decimal? TryCalculatePrice(Pricing pricing, EntityId<SeatType> seatTypeId, DateTime sessionStartTime)
    {
        try
        {
            return priceCalculator.CalculatePrice(pricing, seatTypeId, sessionStartTime);
        }
        catch (PriceNotConfiguredException)
        {
            return null;
        }
    }
}