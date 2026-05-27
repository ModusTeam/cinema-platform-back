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
    IPublishEndpoint publishEndpoint,
    ILogger<SetMyDateOfBirthCommandHandler> logger)
    : IRequestHandler<SetMyDateOfBirthCommand, Result<SetMyDateOfBirthResponse>>
{
    public async Task<Result<SetMyDateOfBirthResponse>> Handle(SetMyDateOfBirthCommand request, CancellationToken ct)
    {
        if (currentUser.UserId == null)
            return Result.Failure<SetMyDateOfBirthResponse>(AuthErrors.UserNotAuthenticated);

        var user = await userManager.FindByIdAsync(currentUser.UserId.ToString()!);
        if (user == null)
            return Result.Failure<SetMyDateOfBirthResponse>(UserErrors.NotFound);

        if (user.DateOfBirth.HasValue)
            return Result.Failure<SetMyDateOfBirthResponse>(UserErrors.DateOfBirthAlreadySet);

        user.DateOfBirth = DateTime.SpecifyKind(request.DateOfBirth.Date, DateTimeKind.Utc);

        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
            return Result.Failure<SetMyDateOfBirthResponse>(
                new Error("User.UpdateFailed", updateResult.Errors.First().Description));

        try
        {
            var integrationEvent = new UserDateOfBirthSetIntegrationEvent(
                UserId: user.Id,
                DateOfBirth: DateOnly.FromDateTime(user.DateOfBirth.Value),
                OccurredAtUtc: DateTime.UtcNow);

            await publishEndpoint.Publish(integrationEvent, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to publish {Event} for User {UserId}. Birthday bonus may be delayed in Loyalty service.",
                nameof(UserDateOfBirthSetIntegrationEvent), user.Id);
        }

        return Result.Success(new SetMyDateOfBirthResponse(user.DateOfBirth.Value));
    }
}
