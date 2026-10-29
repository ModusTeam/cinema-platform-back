using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Catalog.Application.Movies.Validation;
using FluentValidation;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Validators;

public sealed class UpdateMovieImagesValidator : AbstractValidator<UpdateMovieImagesCommand>
{
    public UpdateMovieImagesValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Poster).Must(MovieInputRules.ValidImageReference);
        RuleFor(x => x.Backdrop).Must(MovieInputRules.ValidImageReference);
        RuleFor(x => x.TrailerUrl).Must(MovieInputRules.ValidTrailerUrl)
            .WithMessage("Trailer URL must be an absolute HTTPS URL without credentials, at most 500 characters.");
        RuleFor(x => x.PosterUrl).Must(MovieInputRules.ValidTrailerUrl);
        RuleFor(x => x.BackdropUrl).Must(MovieInputRules.ValidTrailerUrl);
        RuleFor(x => x).Must(x => !(x.SetPosterReference && x.SetPosterUrl) && !(x.SetBackdropReference && x.SetBackdropUrl) &&
            !(x.Poster is not null && x.PosterUrl is not null) && !(x.Backdrop is not null && x.BackdropUrl is not null))
            .WithMessage("Use either the image reference or the legacy URL for each image, not both.");
    }
}
