using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Queries.GetLoyaltyTransactions;

public record GetLoyaltyTransactionsQuery(int Limit = 20, int Skip = 0)
    : IRequest<Result<LoyaltyTransactionHistoryVm>>;

public record LoyaltyTransactionHistoryVm(
    IReadOnlyCollection<LoyaltyTransactionVm> Transactions,
    int TotalCount,
    int Limit,
    int Skip);

public record LoyaltyTransactionVm(
    string Id,
    string Type,
    int Points,
    int BalanceAfter,
    string? OrderId,
    string Description,
    string CreatedAt);

public class GetLoyaltyTransactionsQueryHandler(
    ILoyaltyService loyaltyService,
    ICurrentUserService currentUser)
    : IRequestHandler<GetLoyaltyTransactionsQuery, Result<LoyaltyTransactionHistoryVm>>
{
    public async Task<Result<LoyaltyTransactionHistoryVm>> Handle(
        GetLoyaltyTransactionsQuery request,
        CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null)
            return Result.Failure<LoyaltyTransactionHistoryVm>(new Error("Auth.Required", "User not authenticated"));

        var limit = Math.Clamp(request.Limit, 1, 100);
        var skip = Math.Max(request.Skip, 0);
        var history = await loyaltyService.GetUserTransactionHistoryAsync(userId.Value, limit, skip, ct);

        var transactions = history.Transactions
            .Select(t => new LoyaltyTransactionVm(
                t.Id,
                t.Type,
                t.Points,
                t.BalanceAfter,
                t.OrderId,
                t.Description,
                t.CreatedAt))
            .ToList();

        return Result.Success(new LoyaltyTransactionHistoryVm(
            transactions,
            history.TotalCount,
            limit,
            skip));
    }
}
