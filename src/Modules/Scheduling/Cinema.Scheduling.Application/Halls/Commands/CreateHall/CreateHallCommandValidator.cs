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
            {
                RuleFor(x => x.Seats!)
                    .Cascade(CascadeMode.Stop)
                    .NotEmpty().WithMessage("Seats must contain at least one seat.")
                    .Must(seats => seats!.Count <= 10_000)
                    .WithMessage("A sparse layout may contain at most 10,000 seats.")
                    .Must(seats => seats!.All(s => s is not null))
                    .WithMessage("Seat definitions cannot be null.")
                    .Must(seats => seats!.Select(s => (s.GridX, s.GridY)).Distinct().Count() == seats.Count)
                    .WithMessage("Seat coordinates must be unique.")
                    .Must((command, seats) => seats!.All(s => s.GridX <= command.SeatsPerRow && s.GridY <= command.Rows))
                    .WithMessage("Seat coordinates must be within the hall dimensions.");

                RuleFor(x => x.Rows)
                    .LessThanOrEqualTo(1_000).WithMessage("Sparse layouts may have at most 1,000 rows.");

                RuleFor(x => x.SeatsPerRow)
                    .LessThanOrEqualTo(1_000).WithMessage("Sparse layouts may have at most 1,000 columns.");

                When(x => x.Seats!.All(s => s is not null), () => RuleForEach(x => x.Seats!).ChildRules(seat =>
                {
                    seat.RuleFor(s => s.GridX)
                        .GreaterThan(0).WithMessage("GridX must be greater than zero.");
                    seat.RuleFor(s => s.GridY)
                        .GreaterThan(0).WithMessage("GridY must be greater than zero.");
                    seat.RuleFor(s => s.SeatTypeId)
                        .NotEmpty().WithMessage("SeatTypeId must be a valid GUID.");
                }));
            });
        }
    }
}
