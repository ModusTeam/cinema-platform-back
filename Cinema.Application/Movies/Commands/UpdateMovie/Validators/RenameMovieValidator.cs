using Cinema.Application.Movies.Commands.UpdateMovie.Commands;
using Cinema.Application.Movies.Constants;
using FluentValidation;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Validators;

public class RenameMovieValidator : AbstractValidator<RenameMovieCommand>
{
    public RenameMovieValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.NewTitle).NotEmpty().MaximumLength(MovieConstants.MaxTitleLength);
    }
}