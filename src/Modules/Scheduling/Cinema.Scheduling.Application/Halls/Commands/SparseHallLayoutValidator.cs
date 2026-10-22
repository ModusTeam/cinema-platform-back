using Cinema.Application.Halls.Commands.CreateHall;
using FluentValidation;

namespace Cinema.Application.Halls.Commands;

public record SparseHallLayout(int Rows, int SeatsPerRow, List<CreateHallSeat> Seats);

public class SparseHallLayoutValidator : AbstractValidator<SparseHallLayout>
{
    public SparseHallLayoutValidator()
    {
        RuleFor(x => x.Rows).InclusiveBetween(1, 1_000);
        RuleFor(x => x.SeatsPerRow).InclusiveBetween(1, 1_000);
        RuleFor(x => x.Seats)
            .Cascade(CascadeMode.Stop)
            .NotEmpty().WithMessage("Seats must contain at least one seat.")
            .Must(seats => seats.Count <= 10_000)
            .WithMessage("A sparse layout may contain at most 10,000 seats.")
            .Must(seats => seats.All(s => s is not null))
            .WithMessage("Seat definitions cannot be null.")
            .Must(seats => seats.Select(s => (s.GridX, s.GridY)).Distinct().Count() == seats.Count)
            .WithMessage("Seat coordinates must be unique.")
            .Must((layout, seats) => seats.All(s => s.GridX <= layout.SeatsPerRow && s.GridY <= layout.Rows))
            .WithMessage("Seat coordinates must be within the hall dimensions.");

        When(x => x.Seats is not null && x.Seats.All(s => s is not null), () =>
            RuleForEach(x => x.Seats).ChildRules(seat =>
            {
                seat.RuleFor(s => s.GridX).GreaterThan(0)
                    .WithMessage("GridX must be greater than zero.");
                seat.RuleFor(s => s.GridY).GreaterThan(0)
                    .WithMessage("GridY must be greater than zero.");
                seat.RuleFor(s => s.SeatTypeId).NotEmpty()
                    .WithMessage("SeatTypeId must be a valid GUID.");
            }));
    }
}
