using FluentValidation;

namespace Cinema.Application.Seats.Commands.UpdateSeatType;

public class UpdateSeatTypeCommandValidator : AbstractValidator<UpdateSeatTypeCommand>
{
    public UpdateSeatTypeCommandValidator()
    {
        RuleFor(x => x.Id)
            .NotEmpty();

        RuleFor(x => x.Name)
            .NotEmpty()
            .MaximumLength(100);

        RuleFor(x => x.Description)
            .MaximumLength(500)
            .When(x => x.Description is not null);
    }
}
