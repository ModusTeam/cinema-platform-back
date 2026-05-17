using Cinema.Application.Common.Interfaces;
using Cinema.Infrastructure.Grpc.Loyalty;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Services;

public class GrpcLoyaltyService(
    LoyaltyService.LoyaltyServiceClient client, 
    ILogger<GrpcLoyaltyService> logger) : ILoyaltyService
{
    public async Task<(int Points, string Tier)> GetUserLoyaltyAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var request = new GetBalanceRequest { UserId = userId.ToString() };
            var response = await client.GetBalanceAsync(request, cancellationToken: ct);
            return (response.Balance, response.Tier.ToString());
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch loyalty for user {UserId}. Defaulting to 0/BRONZE.", userId);
            return (0, "BRONZE");
        }
    }

    public async Task<(bool Success, int BalanceAfter, string Error)> DeductPointsAsync(
        Guid userId, int amount, Guid orderId, string idempotencyKey, CancellationToken ct = default)
    {
        try
        {
            var request = new DeductPointsRequest
            {
                UserId = userId.ToString(),
                Amount = amount,
                OrderId = orderId.ToString(),
                IdempotencyKey = idempotencyKey
            };
            
            var response = await client.DeductPointsAsync(request, cancellationToken: ct);
            return (response.Success, response.BalanceAfter, response.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC DeductPoints failed for user {UserId}", userId);
            return (false, 0, "Internal Loyalty Service Error");
        }
    }

    public async Task<(bool Success, string Error)> UseGoldUpgradeAsync(Guid userId, Guid orderId, CancellationToken ct = default)
    {
        try
        {
            var request = new UseGoldUpgradeRequest
            {
                UserId = userId.ToString(),
                OrderId = orderId.ToString()
            };
            
            var response = await client.UseGoldUpgradeAsync(request, cancellationToken: ct);
            return (response.Success, response.ErrorMessage);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "gRPC UseGoldUpgrade failed for user {UserId}", userId);
            return (false, "Internal Loyalty Service Error");
        }
    }
}