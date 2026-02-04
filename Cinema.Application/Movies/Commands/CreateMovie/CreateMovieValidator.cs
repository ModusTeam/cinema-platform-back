using FluentValidation;

namespace Cinema.Application.Movies.Commands.CreateMovie;

public class CreateMovieValidator : AbstractValidator<CreateMovieCommand>
{
    public CreateMovieValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(2000);
        RuleFor(x => x.DurationMinutes).GreaterThan(0);
        RuleFor(x => x.ReleaseYear).GreaterThan(1890);
        RuleFor(x => x.Status).IsInEnum();
    }
}