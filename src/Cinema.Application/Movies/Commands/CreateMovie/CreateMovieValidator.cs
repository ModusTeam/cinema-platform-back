using Cinema.Application.Movies.Constants;
using FluentValidation;

namespace Cinema.Application.Movies.Commands.CreateMovie;

public class CreateMovieValidator : AbstractValidator<CreateMovieCommand>
{
    public CreateMovieValidator()
    {
        RuleFor(x => x.Title)
            .NotEmpty()
            .MaximumLength(MovieConstants.MaxTitleLength);

        RuleFor(x => x.Description)
            .NotEmpty()
            .MaximumLength(MovieConstants.MaxDescriptionLength);

        RuleFor(x => x.DurationMinutes)
            .GreaterThan(0);

        RuleFor(x => x.ReleaseYear)
            .GreaterThanOrEqualTo(MovieConstants.EarliestReleaseYear);

        RuleFor(x => x.Status)
            .IsInEnum();
    }
}