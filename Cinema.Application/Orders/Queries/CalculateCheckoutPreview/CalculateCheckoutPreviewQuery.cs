using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Queries.CalculateCheckoutPreview;

public record CalculateCheckoutPreviewQuery(
    Guid SessionId,
    List<Guid> SeatIds,
    bool UseLoyaltyPoints,
    bool ApplyGoldUpgrade) : IRequest<Result<CheckoutPreviewDto>>;

public record CheckoutPreviewDto(
    string Currency,
    bool CanCheckout,
    decimal OriginalAmount,
    decimal AmountAfterGoldUpgrade,
    decimal FinalAmountToPay,
    decimal TotalDiscountAmount,
    CheckoutGoldUpgradePreviewDto GoldUpgrade,
    CheckoutLoyaltyPointsPreviewDto LoyaltyPoints,
    IReadOnlyCollection<CheckoutTicketPreviewDto> Tickets,
    IReadOnlyCollection<string> Warnings);

public record CheckoutGoldUpgradePreviewDto(
    bool Requested,
    bool CanApply,
    bool Applied,
    decimal DiscountAmount,
    Guid? SeatId,
    decimal? PriceBefore,
    decimal? PriceAfter,
    string? Reason);

public record CheckoutLoyaltyPointsPreviewDto(
    bool Requested,
    bool CanApply,
    bool Applied,
    int PointsToDeduct,
    decimal DiscountAmount,
    decimal AmountToPay,
    string? Reason);

public record CheckoutTicketPreviewDto(
    Guid SeatId,
    string SeatType,
    decimal OriginalPrice,
    decimal FinalPriceAfterGoldUpgrade,
    decimal GoldDiscountAmount,
    bool IsGoldUpgraded);