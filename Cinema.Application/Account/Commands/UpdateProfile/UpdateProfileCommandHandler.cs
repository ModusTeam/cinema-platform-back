using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Cinema.Application.Account.Commands.UpdateProfile;

public class UpdateProfileCommandHandler(
    ICurrentUserService currentUser,
    UserManager<User> userManager) 
    : IRequestHandler<UpdateProfileCommand, Result>
{
    public async Task<Result> Handle(UpdateProfileCommand request, CancellationToken ct)
    {
        if (currentUser.UserId == null) 
            return Result.Failure(new Error("Auth.Unauthorized", "User is not authenticated."));

        var user = await userManager.FindByIdAsync(currentUser.UserId.ToString()!);
        if (user == null) 
            return Result.Failure(new Error("User.NotFound", "User not found."));

        user.FirstName = request.FirstName;
        user.LastName = request.LastName;

        var result = await userManager.UpdateAsync(user);

        if (!result.Succeeded)
        {
            return Result.Failure(new Error("User.UpdateFailed", result.Errors.First().Description));
        }

        return Result.Success();
    }
}