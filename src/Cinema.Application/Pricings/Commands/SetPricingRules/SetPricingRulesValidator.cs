using FluentValidation;

namespace Cinema.Application.Pricings.Commands.SetPricingRules;

public class SetPricingRulesValidator : AbstractValidator<SetPricingRulesCommand>
{
    public SetPricingRulesValidator()
    {
        RuleFor(x => x.PricingId).NotEmpty();
        RuleForEach(x => x.Rules).ChildRules(rule =>
        {
            rule.RuleFor(r => r.Price).GreaterThan(0);
            rule.RuleFor(r => r.SeatTypeId).NotEmpty();
            rule.RuleFor(r => r.DayOfWeek).IsInEnum();
        });
    }
}