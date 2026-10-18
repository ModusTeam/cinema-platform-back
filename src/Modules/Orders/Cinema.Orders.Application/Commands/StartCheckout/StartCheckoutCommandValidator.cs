using FluentValidation;

namespace Cinema.Application.Orders.Commands.StartCheckout;

public class StartCheckoutCommandValidator : AbstractValidator<StartCheckoutCommand>
{
    public StartCheckoutCommandValidator()
    {
        RuleFor(command => command.OrderId).NotEmpty();
        RuleFor(command => command.PaymentToken).NotEmpty();
    }
}
