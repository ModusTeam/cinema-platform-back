using FluentValidation;

namespace Cinema.Application.Orders.Commands.CreateOrder;

public class ReserveOrderCommandValidator : AbstractValidator<ReserveOrderCommand>
{
    public ReserveOrderCommandValidator()
    {
        RuleFor(command => command.SessionId).NotEmpty();
        RuleFor(command => command.SeatIds)
            .NotEmpty()
            .Must(ids => ids.Count <= 10).WithMessage("You cannot purchase more than 10 tickets at once.")
            .Must(ids => ids.Distinct().Count() == ids.Count).WithMessage("Duplicate seats selected.");
    }
}
