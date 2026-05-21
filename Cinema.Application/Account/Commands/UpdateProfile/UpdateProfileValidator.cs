using FluentValidation;

namespace Cinema.Application.Account.Commands.UpdateProfile;

public class UpdateProfileValidator : AbstractValidator<UpdateProfileCommand>
{
    public UpdateProfileValidator()
    {
        RuleFor(x => x.FirstName).MaximumLength(50).NotEmpty();
        RuleFor(x => x.LastName).MaximumLength(50).NotEmpty();

        When(x => x.DateOfBirth.HasValue, () =>
        {
            RuleFor(x => x.DateOfBirth!.Value)
                .LessThan(DateTime.UtcNow.Date)
                .WithMessage("Date of birth must be in the past.");

            RuleFor(x => x.DateOfBirth!.Value)
                .LessThanOrEqualTo(DateTime.UtcNow.Date.AddYears(-10))
                .WithMessage("User must be at least 10 years old.");
        });
    }
}