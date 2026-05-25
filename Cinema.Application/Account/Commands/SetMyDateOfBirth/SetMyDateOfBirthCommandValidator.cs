using FluentValidation;

namespace Cinema.Application.Account.Commands.SetMyDateOfBirth;

public class SetMyDateOfBirthCommandValidator : AbstractValidator<SetMyDateOfBirthCommand>
{
    public SetMyDateOfBirthCommandValidator()
    {
        RuleFor(x => x.DateOfBirth)
            .NotEmpty()
            .LessThanOrEqualTo(DateTime.UtcNow.Date)
            .WithMessage("Date of birth cannot be in the future.")
            .GreaterThanOrEqualTo(DateTime.UtcNow.Date.AddYears(-120))
            .WithMessage("Date of birth is unreasonably old.");
    }
}
