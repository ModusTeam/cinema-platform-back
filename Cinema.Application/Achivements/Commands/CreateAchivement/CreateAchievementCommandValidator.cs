using Cinema.Application.Achievements.Commands;
using FluentValidation;

namespace Cinema.Application.Achievements.Validators;

public class CreateAchievementCommandValidator : AbstractValidator<CreateAchievementCommand>
{
    public CreateAchievementCommandValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code is required.")
            .MaximumLength(64).WithMessage("Code must not exceed 64 characters.")
            .Matches("^[A-Z0-9_]+$").WithMessage("Code must be uppercase letters, digits, or underscores.");

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name is required.")
            .MaximumLength(128);

        RuleFor(x => x.Description)
            .NotEmpty().WithMessage("Description is required.");

        RuleFor(x => x.Icon)
            .NotEmpty().WithMessage("Icon is required.");

        RuleFor(x => x.Category)
            .InclusiveBetween(1, 7).WithMessage("Invalid category.");

        RuleFor(x => x.Rarity)
            .InclusiveBetween(1, 5).WithMessage("Invalid rarity.");

        RuleFor(x => x.Strategy)
            .InclusiveBetween(1, 3).WithMessage("Invalid strategy.");

        RuleFor(x => x.CriteriaJson)
            .NotEmpty().WithMessage("CriteriaJson is required.")
            .Must(BeValidJson).WithMessage("CriteriaJson must be valid JSON with field, operator, target.");

        RuleFor(x => x.RewardPoints)
            .GreaterThanOrEqualTo(0).WithMessage("RewardPoints cannot be negative.");
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