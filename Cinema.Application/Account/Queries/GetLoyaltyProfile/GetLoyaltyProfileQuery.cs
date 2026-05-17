using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Account.Queries.GetLoyaltyProfile;

public record GetLoyaltyProfileQuery : IRequest<Result<LoyaltyProfileVm>>;

public record LoyaltyProfileVm(int Points, string Tier);

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

        var (points, tier) = await loyaltyService.GetUserLoyaltyAsync(userId.Value, ct);
        
        return Result.Success(new LoyaltyProfileVm(points, tier));
    }
}