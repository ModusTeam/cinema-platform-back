using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Queries.GetLoyaltyProfile;

public record GetLoyaltyProfileQuery : IRequest<Result<LoyaltyProfileVm>>;

public record LoyaltyProfileVm(
    int Points,
    int Balance,
    int LifetimePoints,
    int YearPoints,
    int YearVisits,
    string Tier,
    string? TierExpiresAt,
    string? BalanceExpiresAt,
    bool IsBirthdayWeek,
    bool GoldUpgradeAvailable);

public class GetLoyaltyProfileQueryHandler(
    ILoyaltyService loyaltyService,
    ICurrentUserService currentUser)
    : IRequestHandler<GetLoyaltyProfileQuery, Result<LoyaltyProfileVm>>
{
    public async Task<Result<LoyaltyProfileVm>> Handle(GetLoyaltyProfileQuery request, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId == null)
            return Result.Failure<LoyaltyProfileVm>(new Error("Auth.Required", "User not authenticated"));

        var profile = await loyaltyService.GetUserLoyaltyProfileAsync(userId.Value, ct);

        return Result.Success(new LoyaltyProfileVm(
            profile.Balance,
            profile.Balance,
            profile.LifetimePoints,
            profile.YearPoints,
            profile.YearVisits,
            profile.Tier,
            profile.TierExpiresAt,
            profile.BalanceExpiresAt,
            profile.IsBirthdayWeek,
            profile.GoldUpgradeAvailable));
    }
}
