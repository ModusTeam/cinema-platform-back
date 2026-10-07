using FluentValidation;
using System;

namespace Cinema.Application.Movies.Commands.CreateMovie
{
    public class CreateMovieCommandValidator : AbstractValidator<CreateMovieCommand>
    {
        public CreateMovieCommandValidator()
        {
            RuleFor(x => x.Title)
                .NotEmpty().WithMessage("Movie title is required.")
                .MaximumLength(255).WithMessage("Movie title must not exceed 255 characters.");

            RuleFor(x => x.Description)
                .NotEmpty().WithMessage("Movie description is required.");

            RuleFor(x => x.DurationMinutes)
                .GreaterThan(30).WithMessage("Duration must be strictly greater than 30 minutes.")
                .LessThan(500).WithMessage("Duration must be less than 500 minutes.");

            RuleFor(x => x.ReleaseYear)
                .GreaterThanOrEqualTo(DateTime.UtcNow.Year).WithMessage("Release year cannot be in the past.");
        }
    }
}
