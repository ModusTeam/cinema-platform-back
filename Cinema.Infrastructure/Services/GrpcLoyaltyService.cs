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
    IOptions<LoyaltySettings> loyaltyOptions) : ILoyaltyService, IAdminLoyaltyService
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


    public async Task<LoyaltyTransactionHistoryDto> GetUserTransactionHistoryAsync(
        Guid userId,
        int limit,
        int skip,
        CancellationToken ct = default)
    {
        var request = new GetTransactionsRequest
        {
            UserId = userId.ToString(),
            Limit = limit,
            Skip = skip
        };

        var response = await client.GetTransactionsAsync(
            request,
            headers: BuildMetadata(),
            cancellationToken: ct);

        var transactions = response.Transactions
            .Select(t => new LoyaltyTransactionDto(
                t.Id,
                t.Type,
                t.Points,
                t.BalanceAfter,
                string.IsNullOrWhiteSpace(t.OrderId) ? null : t.OrderId,
                t.Description,
                t.CreatedAt))
            .ToList();

        return new LoyaltyTransactionHistoryDto(transactions, response.TotalCount);
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

    public async Task<AdminUserBalanceDto> GetUserBalanceAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            var request = new GetAdminUserBalanceRequest { UserId = userId.ToString() };
            var response = await client.GetAdminUserBalanceAsync(request, headers: BuildMetadata(), cancellationToken: ct);
            
            return new AdminUserBalanceDto(response.Tier, response.Balance, response.LifetimePoints);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new KeyNotFoundException(ex.Status.Detail);
        }
    }

    public async Task<AdminTransactionHistoryDto> GetTransactionHistoryAsync(Guid userId, int limit, int skip, CancellationToken ct)
    {
        var request = new GetAdminTransactionHistoryRequest { UserId = userId.ToString(), Limit = limit, Skip = skip };
        var response = await client.GetAdminTransactionHistoryAsync(request, headers: BuildMetadata(), cancellationToken: ct);

        var mappedTransactions = response.Transactions.Select(t => 
            new AdminTransactionDto(t.Id, t.Type, t.Points, t.BalanceAfter, t.OrderId, t.Description, t.CreatedAt));

        return new AdminTransactionHistoryDto(mappedTransactions);
    }

    public async Task<AdminModifyPointsDto> ModifyPointsAsync(Guid userId, string adminId, int points, string reason, CancellationToken ct)
    {
        try
        {
            var request = new ModifyUserPointsRequest
            {
                UserId = userId.ToString(),
                AdminId = adminId,
                Points = points,
                Reason = reason
            };

            var response = await client.ModifyUserPointsAsync(request, headers: BuildMetadata(), cancellationToken: ct);
            
            return new AdminModifyPointsDto(Guid.Parse(response.UserId), response.Tier, response.Balance, response.Success);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException(ex.Status.Detail);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new KeyNotFoundException(ex.Status.Detail);
        }
    }

    public async Task<AdminUsersListDto> GetUsersAsync(int limit, int skip, string? tierFilter, IEnumerable<Guid>? userIds, CancellationToken ct)
    {
        var request = new GetAdminUsersRequest 
        { 
            Limit = limit, 
            Skip = skip, 
            TierFilter = tierFilter ?? string.Empty 
        };
        
        if (userIds != null)
        {
            request.UserIds.AddRange(userIds.Select(id => id.ToString()));
        }

        var response = await client.GetAdminUsersAsync(request, headers: BuildMetadata(), cancellationToken: ct);

        var profiles = response.Profiles.Select(p => 
            new AdminUserProfileDto(Guid.Parse(p.UserId), string.Empty, string.Empty, p.Tier, p.Balance, p.LifetimePoints));

        return new AdminUsersListDto(profiles, response.TotalCount);
    }

    public async Task<AdminGrantVipDto> GrantVipStatusAsync(Guid userId, string adminId, string reason, CancellationToken ct)
    {
        try
        {
            var request = new GrantVipStatusRequest
            {
                UserId = userId.ToString(),
                AdminId = adminId,
                Reason = reason
            };

            var response = await client.GrantVipStatusAsync(request, headers: BuildMetadata(), cancellationToken: ct);
            return new AdminGrantVipDto(Guid.Parse(response.UserId), response.NewTier, response.Success);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            throw new InvalidOperationException(ex.Status.Detail);
        }
    }


}

