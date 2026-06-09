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
    string? ReasonCode,
    string? Reason);

public record CheckoutLoyaltyPointsPreviewDto(
    bool Requested,
    bool CanApply,
    bool Applied,
    int PointsToDeduct,
    decimal DiscountAmount,
    decimal AmountToPay,
    string? ReasonCode,
    string? Reason);

public record CheckoutTicketPreviewDto(
    Guid SeatId,
    string SeatType,
    decimal OriginalPrice,
    decimal FinalPriceAfterGoldUpgrade,
    decimal GoldDiscountAmount,
    bool IsGoldUpgraded);

public static class CheckoutPreviewReasonCodes
{
    public const string GoldUpgradeRequiresGoldTier = "GOLD_UPGRADE_REQUIRES_GOLD_TIER";
    public const string GoldUpgradeAlreadyUsedThisMonth = "GOLD_UPGRADE_ALREADY_USED_THIS_MONTH";
    public const string GoldUpgradeNoEligibleTicket = "GOLD_UPGRADE_NO_ELIGIBLE_TICKET";

    public const string LoyaltyPointsNotAllowedForSession = "LOYALTY_POINTS_NOT_ALLOWED_FOR_SESSION";
    public const string LoyaltyPointsInsufficientBalance = "LOYALTY_POINTS_INSUFFICIENT_BALANCE";
}
