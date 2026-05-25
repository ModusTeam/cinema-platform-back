using Cinema.Application.Achievements.Commands;
using FluentValidation;

namespace Cinema.Application.Achievements.Validators;

public class UpdateAchievementCommandValidator : AbstractValidator<UpdateAchievementCommand>
{
    public UpdateAchievementCommandValidator()
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage("Id is required.");
        RuleFor(x => x.Code).NotEmpty().MaximumLength(64)
            .Matches("^[A-Z0-9_]+$").WithMessage("Code must be uppercase letters, digits, or underscores.");
        RuleFor(x => x.Name).NotEmpty().MaximumLength(128);
        RuleFor(x => x.Description).NotEmpty();
        RuleFor(x => x.Icon).NotEmpty();
        RuleFor(x => x.Category).InclusiveBetween(1, 7);
        RuleFor(x => x.Rarity).InclusiveBetween(1, 5);
        RuleFor(x => x.Strategy).InclusiveBetween(1, 3);
        RuleFor(x => x.CriteriaJson).NotEmpty()
            .Must(BeValidJson).WithMessage("CriteriaJson must be valid JSON with field, operator, target.");
        RuleFor(x => x.RewardPoints).GreaterThanOrEqualTo(0);
    }

    private static bool BeValidJson(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.TryGetProperty("field", out _) &&
                   root.TryGetProperty("operator", out _) &&
                   root.TryGetProperty("target", out _);
        }
        catch { return false; }
    }
}