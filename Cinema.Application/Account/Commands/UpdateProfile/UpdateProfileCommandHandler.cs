using System.Globalization;
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

        var hadDateOfBirth = user.DateOfBirth.HasValue;

        user.FirstName = request.FirstName;
        user.LastName = request.LastName;

        if (user.DateOfBirth.HasValue
            && request.DateOfBirth.HasValue
            && user.DateOfBirth.Value.Date != request.DateOfBirth.Value.Date)
        {
            return Result.Failure(new Error("User.DateOfBirthAlreadySet", "Date of birth has already been set and cannot be changed."));
        }

        user.DateOfBirth = request.DateOfBirth.HasValue
            ? DateTime.SpecifyKind(request.DateOfBirth.Value, DateTimeKind.Utc)
            : user.DateOfBirth;

        var result = await userManager.UpdateAsync(user);

        if (!result.Succeeded)
            return Result.Failure(new Error("User.UpdateFailed", result.Errors.First().Description));

        var shouldSendDateOfBirthSetEvent = !hadDateOfBirth && user.DateOfBirth.HasValue;

        try
        {
            var endpoint = await sendEndpointProvider.GetSendEndpoint(
                new Uri("queue:user.profile_updated"));

            var message = new UserProfileUpdatedMessage(
                "user.profile_updated",
                new UserProfileUpdatedPayload(user.Id, user.DateOfBirth));

            await endpoint.Send(message, ct);

            if (shouldSendDateOfBirthSetEvent)
            {
                var loyaltyEndpoint = await sendEndpointProvider.GetSendEndpoint(
                    new Uri("queue:loyalty_ticket_purchased"));

                var dateOfBirth = DateOnly
                    .FromDateTime(user.DateOfBirth!.Value)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                var dateOfBirthEvent = new NestJsUserDateOfBirthSetEvent(
                    LoyaltyEventPatterns.UserDateOfBirthSet,
                    new UserDateOfBirthSetPayload(
                        UserId: user.Id,
                        DateOfBirth: dateOfBirth,
                        OccurredAtUtc: DateTime.UtcNow));

                await loyaltyEndpoint.Send(dateOfBirthEvent, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to publish profile integration messages for User {UserId}. DateOfBirth may be stale in Loyalty service.",
                user.Id);
        }

        return Result.Success();
    }
}
