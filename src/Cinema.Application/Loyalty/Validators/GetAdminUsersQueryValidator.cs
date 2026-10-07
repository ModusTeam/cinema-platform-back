using FluentValidation;
using Cinema.Application.AdminLoyalty.Queries;

namespace Cinema.Application.AdminLoyalty.Validators
{
    public class GetAdminUsersQueryValidator : AbstractValidator<GetAdminUsersQuery>
    {
        public GetAdminUsersQueryValidator()
        {
            RuleFor(x => x.Limit)
                .InclusiveBetween(1, 100)
                .WithMessage("Limit must be between 1 and 100.");

            RuleFor(x => x.Skip)
                .GreaterThanOrEqualTo(0)
                .WithMessage("Skip cannot be negative.");

            RuleFor(x => x.TierFilter)
                .Must(tier => new[] { "BASE", "BRONZE", "SILVER", "GOLD" }.Contains(tier.ToUpper()))
                .When(x => !string.IsNullOrWhiteSpace(x.TierFilter))
                .WithMessage("TierFilter must be one of: BASE, BRONZE, SILVER, GOLD.");
        }
    }
}
