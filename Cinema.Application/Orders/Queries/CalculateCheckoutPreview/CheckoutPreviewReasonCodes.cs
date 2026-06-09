namespace Cinema.Application.Orders.Queries.CalculateCheckoutPreview;

public static class CheckoutPreviewReasonCodes
{
    public const string GoldUpgradeRequiresGoldTier = "GOLD_UPGRADE_REQUIRES_GOLD_TIER";
    public const string GoldUpgradeAlreadyUsedThisMonth = "GOLD_UPGRADE_ALREADY_USED_THIS_MONTH";
    public const string GoldUpgradeNoEligibleTicket = "GOLD_UPGRADE_NO_ELIGIBLE_TICKET";

    public const string LoyaltyPointsNotAllowedForSession = "LOYALTY_POINTS_NOT_ALLOWED_FOR_SESSION";
    public const string LoyaltyPointsInsufficientBalance = "LOYALTY_POINTS_INSUFFICIENT_BALANCE";
}
