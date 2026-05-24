using Cinema.Application.Common.Interfaces;
using Cinema.Infrastructure.Grpc.Loyalty;
using Cinema.Infrastructure.Options;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Cinema.Infrastructure.Services;

public class GrpcLoyaltyService(
    LoyaltyService.LoyaltyServiceClient client,
    ILogger<GrpcLoyaltyService> logger,
    IOptions<LoyaltySettings> loyaltyOptions) : ILoyaltyService
{
    private readonly string _apiKey = loyaltyOptions.Value.ApiKey
        ?? throw new InvalidOperationException("LoyaltySettings:ApiKey is not configured.");

    private Metadata BuildMetadata() => new() { { "x-api-key", _apiKey } };

    public async Task<(int Points, string Tier)> GetUserLoyaltyAsync(
        Guid userId, CancellationToken ct = default)
    {
        var response = await client.GetBalanceAsync(
            new GetBalanceRequest { UserId = userId.ToString() },
            headers: BuildMetadata(), cancellationToken: ct);

        return (response.Balance, response.Tier.ToString());
    }

    public async Task<(bool IsAllowed, int PointsToDeduct, decimal AmountToPay)> CalculateDiscountAsync(
        Guid userId, decimal orderAmount, CancellationToken ct = default)
    {
        var response = await client.CalculateDiscountAsync(
            new CalculateDiscountRequest
            {
                UserId = userId.ToString(),
                OrderAmount = (double)orderAmount
            },
            headers: BuildMetadata(), cancellationToken: ct);

        return (response.IsAllowed, response.PointsToDeduct, (decimal)response.AmountToPay);
    }

    public async Task<(bool Success, int BalanceAfter, string Error)> DeductPointsAsync(
        Guid userId, int amount, Guid orderId, string idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            var response = await client.DeductPointsAsync(
                new DeductPointsRequest
                {
                    UserId = userId.ToString(),
                    Amount = amount,
                    OrderId = orderId.ToString(),
                    IdempotencyKey = idempotencyKey
                },
                headers: BuildMetadata(), cancellationToken: ct);

            return (response.Success, response.BalanceAfter, response.ErrorMessage);
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "gRPC DeductPoints failed for user {UserId}.", userId);
            return (false, 0, $"Loyalty service error: {ex.Status.Detail}");
        }
    }

    public async Task<(bool Success, string Error)> RefundPointsAsync(
        Guid userId, int amount, Guid orderId, string idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            var response = await client.RefundPointsAsync(
                new RefundPointsRequest
                {
                    UserId = userId.ToString(),
                    Amount = amount,
                    OrderId = orderId.ToString(),
                    IdempotencyKey = idempotencyKey
                },
                headers: BuildMetadata(), cancellationToken: ct);

            return (response.Success, response.ErrorMessage);
        }
        catch (RpcException ex)
        {
            logger.LogError(ex,
                "gRPC RefundPoints failed for user {UserId}, Order {OrderId}. Manual reconciliation may be required.",
                userId, orderId);
            return (false, $"Loyalty service error during refund: {ex.Status.Detail}");
        }
    }

    public async Task<(bool Success, string Error)> UseGoldUpgradeAsync(
        Guid userId, Guid orderId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.UseGoldUpgradeAsync(
                new UseGoldUpgradeRequest
                {
                    UserId = userId.ToString(),
                    OrderId = orderId.ToString()
                },
                headers: BuildMetadata(), cancellationToken: ct);

            return (response.Success, response.ErrorMessage);
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "gRPC UseGoldUpgrade failed for user {UserId}.", userId);
            return (false, $"Loyalty service error: {ex.Status.Detail}");
        }
    }

    public async Task<(bool Success, string Error)> RollbackGoldUpgradeAsync(
        Guid userId, Guid orderId, CancellationToken ct = default)
    {
        try
        {
            var response = await client.RollbackGoldUpgradeAsync(
                new RollbackGoldUpgradeRequest
                {
                    UserId = userId.ToString(),
                    OrderId = orderId.ToString()
                },
                headers: BuildMetadata(), cancellationToken: ct);

            return (response.Success, response.ErrorMessage);
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "gRPC RollbackGoldUpgrade failed for user {UserId}, Order {OrderId}.", userId, orderId);
            return (false, $"Loyalty service error: {ex.Status.Detail}");
        }
    }
}
