using Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Commands;
using FluentValidation;

namespace Cinema.Catalog.Application.Movies.Commands.UpdateMovie.Validators;

public sealed class UpdateMovieStatusValidator : AbstractValidator<UpdateMovieStatusCommand>
{
    public UpdateMovieStatusValidator()
    {
        RuleFor(x => x.Id).NotEmpty();
        RuleFor(x => x.Status).IsInEnum();
    }
}
