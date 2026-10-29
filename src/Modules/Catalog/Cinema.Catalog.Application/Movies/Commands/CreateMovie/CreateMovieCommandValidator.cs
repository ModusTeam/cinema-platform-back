using Cinema.Catalog.Application.Movies.Constants;
using Cinema.Catalog.Application.Movies.Validation;
using FluentValidation;

namespace Cinema.Catalog.Application.Movies.Commands.CreateMovie;

public sealed class CreateMovieCommandValidator : AbstractValidator<CreateMovieCommand>
{
    public CreateMovieCommandValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(MovieConstants.MaxTitleLength);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(MovieConstants.MaxDescriptionLength);
        RuleFor(x => x.DurationMinutes).InclusiveBetween(1, MovieConstants.MaxDurationMinutes);
        RuleFor(x => x.ReleaseYear).InclusiveBetween(MovieConstants.EarliestReleaseYear, DateTime.UtcNow.Year + 10);
        RuleFor(x => x.Status).IsInEnum();
        RuleFor(x => x.GenreIds).Must(MovieInputRules.ValidGenreIds)
            .WithMessage("Genre IDs must be non-empty, unique GUIDs (at most 50).");
        RuleFor(x => x.Rating).Must(x => x is null || MovieInputRules.ValidRating(x.Value))
            .WithMessage("Rating must be between 0 and 10 with at most two decimal places.");
        RuleFor(x => x.AgeRestriction).Must(MovieInputRules.ValidAgeRestriction)
            .WithMessage("Age restriction must be 0+, 7+, 12+, 16+ or 18+.");
        RuleFor(x => x.TrailerUrl).Must(MovieInputRules.ValidTrailerUrl)
            .WithMessage("Trailer URL must be an absolute HTTPS URL without credentials, at most 500 characters.");
        RuleFor(x => x.Poster).Must(MovieInputRules.ValidImageReference)
            .WithMessage("Poster must contain an HTTPS secureUrl and a publicId.");
        RuleFor(x => x.Backdrop).Must(MovieInputRules.ValidImageReference)
            .WithMessage("Backdrop must contain an HTTPS secureUrl and a publicId.");
        RuleFor(x => x.Cast).Must(MovieInputRules.ValidCast)
            .WithMessage("Cast supports at most 50 unique name/role pairs; names are required and names/roles must not exceed 200 characters.");
    }
}
