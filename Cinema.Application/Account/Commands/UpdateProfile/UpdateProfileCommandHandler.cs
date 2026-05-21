using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Account.Commands.UpdateProfile;

public class UpdateProfileCommandHandler(
    ICurrentUserService currentUser,
    UserManager<User> userManager,
    ISendEndpointProvider sendEndpointProvider,
    ILogger<UpdateProfileCommandHandler> logger)
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

        if (user.DateOfBirth.HasValue
            && request.DateOfBirth.HasValue
            && user.DateOfBirth.Value.Date != request.DateOfBirth.Value.Date)
        {
            return Result.Failure(new Error("User.UpdateFailed", "Date of birth can only be specified once."));
        }

        user.DateOfBirth = request.DateOfBirth.HasValue
            ? DateTime.SpecifyKind(request.DateOfBirth.Value, DateTimeKind.Utc)
            : user.DateOfBirth;

        var result = await userManager.UpdateAsync(user);

        if (!result.Succeeded)
            return Result.Failure(new Error("User.UpdateFailed", result.Errors.First().Description));

        try
        {
            var endpoint = await sendEndpointProvider.GetSendEndpoint(
                new Uri("queue:user.profile_updated"));

            var message = new UserProfileUpdatedMessage(
                "user.profile_updated",
                new UserProfileUpdatedPayload(user.Id, user.DateOfBirth));

            await endpoint.Send(message, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to publish UserProfileUpdatedMessage for User {UserId}. DateOfBirth may be stale in Loyalty service.",
                user.Id);
        }

        return Result.Success();
    }
}