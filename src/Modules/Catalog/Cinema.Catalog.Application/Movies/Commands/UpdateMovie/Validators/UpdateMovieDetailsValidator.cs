using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Catalog.Application.Movies.Constants;
using Cinema.Catalog.Application.Movies.Validation;
using FluentValidation;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Validators;

public sealed class UpdateMovieDetailsValidator : AbstractValidator<UpdateMovieDetailsCommand>
{
    public UpdateMovieDetailsValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Description).NotEmpty().MaximumLength(MovieConstants.MaxDescriptionLength).When(x => x.Description is not null);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, MovieConstants.MaxDurationMinutes).When(x => x.DurationMinutes.HasValue);
        RuleFor(x => x.Rating).Must(value => value is null || (double.IsFinite(value.Value) &&
            value.Value is >= 0 and <= 10 && Math.Round(value.Value, 2) == value.Value))
            .WithMessage("Rating must be between 0 and 10 with at most two decimal places.");
        RuleFor(x => x.ReleaseYear).InclusiveBetween(MovieConstants.EarliestReleaseYear, DateTime.UtcNow.Year + 10).When(x => x.ReleaseYear.HasValue);
        RuleFor(x => x.GenreIds).Must(MovieInputRules.ValidGenreIds);
        RuleFor(x => x.AgeRestriction).Must(MovieInputRules.ValidAgeRestriction);
        RuleFor(x => x.Cast).Must(MovieInputRules.ValidCast);
    }
}
