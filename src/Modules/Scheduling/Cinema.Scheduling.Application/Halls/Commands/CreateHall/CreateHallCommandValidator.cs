using FluentValidation;

namespace Cinema.Application.Halls.Commands.CreateHall
{
    public class CreateHallCommandValidator : AbstractValidator<CreateHallCommand>
    {
        public CreateHallCommandValidator()
        {
            RuleFor(x => x.Name)
                .Must(name => !string.IsNullOrWhiteSpace(name))
                .WithMessage("Hall name is required.");

            RuleFor(x => x.Rows)
                .GreaterThan(0).WithMessage("Rows must be strictly greater than zero.");

            RuleFor(x => x.SeatsPerRow)
                .GreaterThan(0).WithMessage("Seats per row must be strictly greater than zero.");

            RuleFor(x => x.SeatTypeId)
                .NotEmpty().WithMessage("SeatTypeId must be a valid GUID.");
                
            RuleForEach(x => x.TechnologyIds)
                .NotEmpty().WithMessage("Technology ID must be a valid GUID.");

            RuleFor(x => x.TechnologyIds)
                .Cascade(CascadeMode.Stop)
                .Must(ids => ids is not null && ids.Count == ids.Distinct().Count())
                .WithMessage("Technology IDs must be unique.");

            When(x => x.Seats is not null, () =>
                RuleFor(x => new SparseHallLayout(x.Rows, x.SeatsPerRow, x.Seats!))
                    .SetValidator(new SparseHallLayoutValidator()).OverridePropertyName(string.Empty));
        }
    }
}
