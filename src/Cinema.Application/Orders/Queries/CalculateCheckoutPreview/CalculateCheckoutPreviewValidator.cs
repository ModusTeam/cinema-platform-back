using FluentValidation;

namespace Cinema.Application.Orders.Queries.CalculateCheckoutPreview;

public class CalculateCheckoutPreviewValidator : AbstractValidator<CalculateCheckoutPreviewQuery>
{
    public CalculateCheckoutPreviewValidator()
    {
        RuleFor(x => x.SessionId)
            .NotEmpty().WithMessage("Session ID is required.");

        RuleFor(x => x.SeatIds)
            .NotEmpty().WithMessage("At least one seat must be selected.")
            .Must(ids => ids.Count <= 10).WithMessage("You cannot purchase more than 10 tickets at once.")
            .Must(ids => ids.Distinct().Count() == ids.Count).WithMessage("Duplicate seats selected.");
    }
}