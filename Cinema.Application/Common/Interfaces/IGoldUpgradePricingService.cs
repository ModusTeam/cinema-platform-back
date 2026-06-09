using Cinema.Domain.Entities;
using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IGoldUpgradePricingService
{
    Task<Result<GoldUpgradePricingQuote>> CalculateAsync(
        Session session,
        IReadOnlyCollection<GoldUpgradeTicketPrice> tickets,
        CancellationToken ct = default);
}

public sealed record GoldUpgradeTicketPrice(Guid SeatId, decimal Price);

public sealed record GoldUpgradePricingQuote(
    bool IsApplied,
    decimal BasePrice,
    decimal DiscountAmount,
    Guid? SeatId,
    decimal? PriceBefore,
    decimal? PriceAfter,
    string? Reason);