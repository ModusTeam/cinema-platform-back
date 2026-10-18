using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Commands.StartCheckout;

public class StartCheckoutCommandHandler(
    ICurrentUserService currentUser,
    IOrderCheckoutOrchestrator checkoutOrchestrator) : IRequestHandler<StartCheckoutCommand, Result<Guid>>
{
    public Task<Result<Guid>> Handle(StartCheckoutCommand request, CancellationToken ct)
    {
        Guid? userId = currentUser.UserId;
        if (userId is null)
            return Task.FromResult(Result.Failure<Guid>(new Error("Auth.Required", "User not authenticated")));

        return checkoutOrchestrator.ProcessCheckoutAsync(
            userId.Value, request.OrderId, request.ApplyGoldUpgrade,
            request.UseLoyaltyPoints, request.PaymentToken, ct);
    }
}
