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
            var endpoint = await sendEndpointProvider.GetSendEndpoint(
                new Uri("queue:loyalty_ticket_purchased"));

            var dateOfBirth = DateOnly
                .FromDateTime(user.DateOfBirth.Value)
                .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var integrationEvent = new NestJsUserDateOfBirthSetEvent(
                LoyaltyEventPatterns.UserDateOfBirthSet,
                new UserDateOfBirthSetPayload(
                    UserId: user.Id,
                    DateOfBirth: dateOfBirth,
                    OccurredAtUtc: DateTime.UtcNow));

            await endpoint.Send(integrationEvent, ct);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Failed to send {Event} for User {UserId}. Birthday bonus may be delayed in Loyalty service.",
                nameof(NestJsUserDateOfBirthSetEvent), user.Id);
        }

        return Result.Success(new SetMyDateOfBirthResponse(user.DateOfBirth.Value));
    }
}
