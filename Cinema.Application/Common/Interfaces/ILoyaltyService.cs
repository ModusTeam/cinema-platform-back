using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface ILoyaltyService
{
    Task<(int Points, string Tier)> GetUserLoyaltyAsync(Guid userId, CancellationToken ct = default);

    Task<(bool IsAllowed, int PointsToDeduct, decimal AmountToPay)> CalculateDiscountAsync(
        Guid userId, decimal orderAmount, CancellationToken ct = default);
    Task<(bool Success, int BalanceAfter, string Error)> DeductPointsAsync(
        Guid userId, int amount, Guid orderId, string idempotencyKey, CancellationToken ct = default);

    Task<(bool Success, string Error)> RefundPointsAsync(
        Guid userId, int amount, Guid orderId, string idempotencyKey, CancellationToken ct = default);

    Task<(bool Success, string Error)> UseGoldUpgradeAsync(
        Guid userId, Guid orderId, CancellationToken ct = default);
}