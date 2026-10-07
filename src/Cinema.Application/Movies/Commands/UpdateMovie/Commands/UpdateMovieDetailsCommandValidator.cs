using FluentValidation;
using System;

namespace Cinema.Application.Movies.Commands.UpdateMovie.Commands
{
    public class UpdateMovieDetailsCommandValidator : AbstractValidator<UpdateMovieDetailsCommand>
    {
        public UpdateMovieDetailsCommandValidator()
        {
            RuleFor(x => x.Id)
                .NotEmpty().WithMessage("Movie ID must be a valid GUID.");

            RuleFor(x => x.Description)
                .NotEmpty().WithMessage("Movie description cannot be empty if provided.")
                .When(x => x.Description != null);

            RuleFor(x => x.DurationMinutes)
                .GreaterThan(30).WithMessage("Duration must be strictly greater than 30 minutes.")
                .LessThan(500).WithMessage("Duration must be less than 500 minutes.")
                .When(x => x.DurationMinutes.HasValue);
        }
    }
}
