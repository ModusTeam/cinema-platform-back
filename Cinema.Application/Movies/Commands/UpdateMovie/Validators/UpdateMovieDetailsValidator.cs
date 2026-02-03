using Cinema.Application.Movies.Commands.UpdateMovie.Commands;
using FluentValidation;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Validators;

public class UpdateMovieDetailsValidator : AbstractValidator<UpdateMovieDetailsCommand>
{
    public UpdateMovieDetailsValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.DurationMinutes).GreaterThan(0).When(x => x.DurationMinutes.HasValue);
        RuleFor(x => x.Rating).InclusiveBetween(0, 10).When(x => x.Rating.HasValue);
    }
}