using FluentValidation;
using Cinema.Application.AdminLoyalty.GrantVipStatus;

namespace Cinema.Application.AdminLoyalty.Validators
{
    public class GrantVipStatusCommandValidator : AbstractValidator<GrantVipStatusCommand>
    {
        public GrantVipStatusCommandValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty()
                .WithMessage("UserId cannot be empty.");

            RuleFor(x => x.AdminId)
                .NotEmpty()
                .WithMessage("AdminId cannot be empty.");

            RuleFor(x => x.Reason)
                .MinimumLength(5)
                .WithMessage("Reason must be at least 5 characters long.");
        }
    }
}
