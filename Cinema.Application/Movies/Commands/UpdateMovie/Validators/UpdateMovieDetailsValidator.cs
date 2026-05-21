using Cinema.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Application.Movies.Constants;
using FluentValidation;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Validators;

public class UpdateMovieDetailsValidator : AbstractValidator<UpdateMovieDetailsCommand>
{
    public UpdateMovieDetailsValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Description).MaximumLength(MovieConstants.MaxDescriptionLength);
        RuleFor(x => x.DurationMinutes).GreaterThan(0).When(x => x.DurationMinutes.HasValue);
        RuleFor(x => x.Rating).InclusiveBetween(0, 10).When(x => x.Rating.HasValue);
        RuleFor(x => x.ReleaseYear).GreaterThanOrEqualTo(MovieConstants.EarliestReleaseYear).When(x => x.ReleaseYear.HasValue);
    }
}