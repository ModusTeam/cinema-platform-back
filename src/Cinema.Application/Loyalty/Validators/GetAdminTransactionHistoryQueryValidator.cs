using FluentValidation;
using Cinema.Application.AdminLoyalty.Queries;

namespace Cinema.Application.AdminLoyalty.Validators
{
    public class GetAdminTransactionHistoryQueryValidator : AbstractValidator<GetAdminTransactionHistoryQuery>
    {
        public GetAdminTransactionHistoryQueryValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty()
                .WithMessage("UserId cannot be empty.");

            RuleFor(x => x.Limit)
                .InclusiveBetween(1, 100)
                .WithMessage("Limit must be between 1 and 100.");

            RuleFor(x => x.Skip)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Skip cannot be negative.");
        }
    }
}
