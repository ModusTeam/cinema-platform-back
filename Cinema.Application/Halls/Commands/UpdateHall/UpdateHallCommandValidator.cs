using FluentValidation;

namespace Cinema.Application.Halls.Commands.UpdateHall
{
    public class UpdateHallCommandValidator : AbstractValidator<UpdateHallCommand>
    {
        public UpdateHallCommandValidator()
        {
            RuleFor(x => x.HallId)
                .NotEmpty().WithMessage("Hall ID must be a valid GUID.");

            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Hall name is required.");
        }
    }
}