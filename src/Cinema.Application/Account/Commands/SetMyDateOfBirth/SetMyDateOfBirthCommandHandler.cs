using System.Globalization;
using Cinema.Application.Common.Contracts;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Errors;
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
    ILogger<SetMyDateOfBirthCommandHandler> logger,
    ICustomerFactsWriter facts)
    : IRequestHandler<SetMyDateOfBirthCommand, Result<SetMyDateOfBirthResponse>>
{
    public async Task<Result<SetMyDateOfBirthResponse>> Handle(SetMyDateOfBirthCommand request, CancellationToken ct)
    {
        if (currentUser.UserId is not { } userId)
            return Result.Failure<SetMyDateOfBirthResponse>(AuthErrors.UserNotAuthenticated);

        DateTime acceptedDate = default;
        Result result = await facts.ExecuteAsync(userId, async token =>
        {
            User? user = await userManager.FindByIdAsync(userId.ToString());
            if (user is null) return Result.Failure(UserErrors.NotFound);
            if (user.DateOfBirth.HasValue) return Result.Failure(UserErrors.DateOfBirthAlreadySet);
            if (user.CustomerFactsRevision == int.MaxValue)
                return Result.Failure(new Error("User.CustomerFactsRevisionExhausted", "Customer facts revision limit reached."));

            user.RecordFirstDateOfBirth(DateOnly.FromDateTime(request.DateOfBirth), DateTime.UtcNow);
            acceptedDate = user.DateOfBirth.GetValueOrDefault();
            try
            {
                ISendEndpoint endpoint = await sendEndpointProvider.GetSendEndpoint(new Uri("queue:loyalty_ticket_purchased"));
                await endpoint.Send(new NestJsUserDateOfBirthSetEvent(LoyaltyEventPatterns.UserDateOfBirthSet,
                    new UserDateOfBirthSetPayload(user.Id,
                        DateOnly.FromDateTime(acceptedDate).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        user.DobFirstSetAtUtc.GetValueOrDefault())), token);
                await facts.StageAsync(user, Guid.NewGuid(), "DateOfBirthSet", token);
            }
            catch (Exception)
            {
                logger.LogError("Customer DOB staging failed for User {UserId}: {Code}", userId, "DOB_STAGING_FAILED");
                return Result.Failure(new Error("User.MessageStagingFailed", "Could not save the date of birth change. Please try again."));
            }
            IdentityResult update = await userManager.UpdateAsync(user);
            return update.Succeeded ? Result.Success()
                : Result.Failure(new Error("User.UpdateFailed", update.Errors.First().Description));
        }, ct);
        return result.IsSuccess ? Result.Success(new SetMyDateOfBirthResponse(acceptedDate))
            : Result.Failure<SetMyDateOfBirthResponse>(result.Error);
    }
}
