using FluentValidation;
using Cinema.Application.AdminLoyalty.Commands;

namespace Cinema.Application.AdminLoyalty.Validators
{
    public class ModifyUserPointsCommandValidator : AbstractValidator<ModifyUserPointsCommand>
    {
        public ModifyUserPointsCommandValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty()
                .WithMessage("UserId cannot be empty.");

            RuleFor(x => x.Points)
                .NotEqual(0)
                .WithMessage("Points cannot be zero.");

            RuleFor(x => x.Reason)
                .MinimumLength(5)
                .WithMessage("Reason must be at least 5 characters long.");
        }
    }
}
