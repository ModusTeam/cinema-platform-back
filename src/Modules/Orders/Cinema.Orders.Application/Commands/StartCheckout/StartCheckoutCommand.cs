using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Orders.Commands.StartCheckout;

public record StartCheckoutCommand(Guid OrderId, string PaymentToken, bool UseLoyaltyPoints, bool ApplyGoldUpgrade)
    : IRequest<Result<Guid>>;
