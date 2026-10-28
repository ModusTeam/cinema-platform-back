using System.Globalization;
using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
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
    ILogger<UpdateProfileCommandHandler> logger,
    ICustomerFactsWriter facts)
    : IRequestHandler<UpdateProfileCommand, Result>
{
    public async Task<Result> Handle(UpdateProfileCommand request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Result.Failure(new Error("Auth.Unauthorized", "User is not authenticated."));

        return await facts.ExecuteAsync(userId, async token =>
        {
            User? user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null) return Result.Failure(new Error("User.NotFound", "User not found."));
            if (user.DateOfBirth.HasValue && request.DateOfBirth.HasValue && user.DateOfBirth.Value.Date != request.DateOfBirth.Value.Date)
                return Result.Failure(new Error("User.DateOfBirthAlreadySet", "Date of birth has already been set and cannot be changed."));

            bool firstDob = !user.DateOfBirth.HasValue && request.DateOfBirth.HasValue;
            if (firstDob && user.CustomerFactsRevision == int.MaxValue)
                return Result.Failure(new Error("User.CustomerFactsRevisionExhausted", "Customer facts revision limit reached."));
            user.FirstName = request.FirstName;
            user.LastName = request.LastName;
            if (firstDob) user.RecordFirstDateOfBirth(DateOnly.FromDateTime(request.DateOfBirth.GetValueOrDefault()), DateTime.UtcNow);

            try
            {
                ISendEndpoint endpoint = await sendEndpointProvider.GetSendEndpoint(new Uri("queue:user.profile_updated"));
                await endpoint.Send(new UserProfileUpdatedMessage("user.profile_updated",
                    new UserProfileUpdatedPayload(user.Id, user.DateOfBirth)), token);
                if (firstDob)
                {
                    ISendEndpoint loyalty = await sendEndpointProvider.GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
                    await loyalty.Send(new NestJsUserDateOfBirthSetEvent(LoyaltyEventPatterns.UserDateOfBirthSet,
                        new UserDateOfBirthSetPayload(user.Id,
                            DateOnly.FromDateTime(user.DateOfBirth.GetValueOrDefault()).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            user.DobFirstSetAtUtc.GetValueOrDefault())), token);
                    await facts.StageAsync(user, Guid.NewGuid(), "DateOfBirthSet", token);
                }
            }
            catch (Exception)
            {
                logger.LogError("Profile staging failed for User {UserId}: {Code}", userId, "PROFILE_STAGING_FAILED");
                return Result.Failure(new Error("User.MessageStagingFailed", "Could not save the profile change. Please try again."));
            }
            IdentityResult update = await userManager.UpdateAsync(user);
            return update.Succeeded ? Result.Success()
                : Result.Failure(new Error("User.UpdateFailed", update.Errors.First().Description));
        }, ct);
    }
}
