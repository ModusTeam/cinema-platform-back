using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.AspNetCore.Identity;

namespace Cinema.Application.Account.Commands.ChangePassword;

public class ChangePasswordCommandHandler(
    ICurrentUserService currentUser,
    UserManager<User> userManager) 
    : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken ct)
    {
        if (currentUser.UserId == null) 
            return Result.Failure(new Error("Auth.Unauthorized", "User is not authenticated."));

        var user = await userManager.FindByIdAsync(currentUser.UserId.ToString()!);
        if (user == null) 
            return Result.Failure(new Error("User.NotFound", "User not found."));

        var result = await userManager.ChangePasswordAsync(user, request.OldPassword, request.NewPassword);

        if (!result.Succeeded)
        {
            return Result.Failure(new Error("User.ChangePasswordFailed", result.Errors.First().Description));
        }

        return Result.Success();
    }
}