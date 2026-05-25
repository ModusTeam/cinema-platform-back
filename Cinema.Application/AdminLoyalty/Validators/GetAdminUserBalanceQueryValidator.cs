using FluentValidation;
using Cinema.Application.AdminLoyalty.Queries;

namespace Cinema.Application.AdminLoyalty.Validators
{
    public class GetAdminUserBalanceQueryValidator : AbstractValidator<GetAdminUserBalanceQuery>
    {
        public GetAdminUserBalanceQueryValidator()
        {
            RuleFor(x => x.UserId)
                .NotEmpty()
                .WithMessage("UserId cannot be empty.");
        }
    }
}
