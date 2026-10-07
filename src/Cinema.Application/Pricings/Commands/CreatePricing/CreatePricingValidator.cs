using FluentValidation;

namespace Cinema.Application.Pricings.Commands.CreatePricing;

public class CreatePricingValidator : AbstractValidator<CreatePricingCommand>
{
    public CreatePricingValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
    }
}