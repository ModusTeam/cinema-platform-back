using FluentValidation;

namespace Cinema.Application.Genres.Commands.CreateGenre;

public class CreateGenreValidator : AbstractValidator<CreateGenreCommand>
{
    public CreateGenreValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.ExternalId).GreaterThan(0);
    }
}