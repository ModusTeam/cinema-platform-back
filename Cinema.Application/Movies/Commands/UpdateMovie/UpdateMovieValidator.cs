using FluentValidation;

namespace Cinema.Application.Movies.Commands.UpdateMovie;

public class UpdateMovieValidator : AbstractValidator<UpdateMovieCommand>
{
    public UpdateMovieValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).MaximumLength(2000);
        RuleFor(x => x.TrailerUrl).MaximumLength(500);
    }
}