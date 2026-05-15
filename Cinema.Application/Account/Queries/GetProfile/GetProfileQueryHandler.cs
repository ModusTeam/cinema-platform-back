using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Cinema.Application.Account.Queries.GetProfile;

public class GetProfileQueryHandler(
    ICurrentUserService currentUser,
    ILoyaltyService loyaltyService,
    UserManager<User> userManager) 
    : IRequestHandler<GetProfileQuery, Result<UserProfileDto>>
{
    public async Task<Result<UserProfileDto>> Handle(GetProfileQuery request, CancellationToken ct)
    {
        if (currentUser.UserId == null) 
            return Result.Failure<UserProfileDto>(new Error("Auth.Unauthorized", "User is not authenticated."));

        var userId = currentUser.UserId;
        var (points, tier) = await loyaltyService.GetUserLoyaltyAsync(userId.Value, ct);
        var user = await userManager.FindByIdAsync(userId.Value.ToString());
        
        if (user == null) 
            return Result.Failure<UserProfileDto>(new Error("User.NotFound", "User profile not found."));

        return Result.Success(new UserProfileDto(
            user.Id,
            user.Email!,
            user.FirstName ?? string.Empty,
            user.LastName ?? string.Empty,
            points,
            tier
        ));
    }
}