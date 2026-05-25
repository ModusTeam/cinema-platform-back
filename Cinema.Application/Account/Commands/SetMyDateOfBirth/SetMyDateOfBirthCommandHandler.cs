using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MassTransit;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace Cinema.Application.Account.Commands.SetMyDateOfBirth;

public class SetMyDateOfBirthCommandHandler(
    ICurrentUserService currentUser,
    UserManager<User> userManager,
    ISendEndpointProvider sendEndpointProvider,
    ILogger<SetMyDateOfBirthCommandHandler> logger)
    : IRequestHandler<SetMyDateOfBirthCommand, Result<SetMyDateOfBirthResponse>>
{
    public async Task<Result<SetMyDateOfBirthResponse>> Handle(SetMyDateOfBirthCommand request, CancellationToken ct)
    {
        if (currentUser.UserId == null)
            return Result.Failure<SetMyDateOfBirthResponse>(new Error("Auth.Unauthorized", "User is not authenticated."));

        var user = await userManager.FindByIdAsync(currentUser.UserId.ToString()!);
        if (user == null)
            return Result.Failure<SetMyDateOfBirthResponse>(new Error("User.NotFound", "User not found."));

        if (user.DateOfBirth.HasValue)
        {
            return Result.Failure<SetMyDateOfBirthResponse>(
                new Error("User.DateOfBirthAlreadySet", "Date of birth has already been set and cannot be changed."));
        }

        user.DateOfBirth = DateTime.SpecifyKind(request.DateOfBirth.Date, DateTimeKind.Utc);

        var result = await userManager.UpdateAsync(user);

        if (!result.Succeeded)
            return Result.Failure<SetMyDateOfBirthResponse>(new Error("User.UpdateFailed", result.Errors.First().Description));

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

        return Result.Success(new SetMyDateOfBirthResponse(user.DateOfBirth.Value));
    }
}
