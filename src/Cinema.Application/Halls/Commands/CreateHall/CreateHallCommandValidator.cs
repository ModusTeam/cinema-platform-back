using FluentValidation;

namespace Cinema.Application.Halls.Commands.CreateHall
{
    public class CreateHallCommandValidator : AbstractValidator<CreateHallCommand>
    {
        public CreateHallCommandValidator()
        {
            RuleFor(x => x.Name)
                .NotEmpty().WithMessage("Hall name is required.");

            RuleFor(x => x.Rows)
                .GreaterThan(0).WithMessage("Rows must be strictly greater than zero.");

            RuleFor(x => x.SeatsPerRow)
                .GreaterThan(0).WithMessage("Seats per row must be strictly greater than zero.");

            RuleFor(x => x.SeatTypeId)
                .NotEmpty().WithMessage("SeatTypeId must be a valid GUID.");
                
            RuleForEach(x => x.TechnologyIds)
                .NotEmpty().WithMessage("Technology ID must be a valid GUID.");
        }
    }
}