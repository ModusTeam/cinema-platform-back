using FluentValidation;

namespace Cinema.Application.Technologies.Commands.CreateTechnology
{
    public class CreateTechnologyCommandValidator : AbstractValidator<CreateTechnologyCommand>
    {
        public CreateTechnologyCommandValidator()
        {
            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Technology name is required.")
                .MaximumLength(100).WithMessage("Technology name must not exceed 100 characters.");

            RuleFor(x => x.Type)
                .NotEmpty().WithMessage("Technology type is required.")
                .MaximumLength(100).WithMessage("Technology type must not exceed 100 characters.");
        }
    }
}