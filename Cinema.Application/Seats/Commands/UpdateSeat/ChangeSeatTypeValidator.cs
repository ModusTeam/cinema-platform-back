using FluentValidation;

namespace Cinema.Application.Seats.Commands.UpdateSeat;

public class ChangeSeatTypeValidator : AbstractValidator<ChangeSeatTypeCommand>
{
    public ChangeSeatTypeValidator()
    {
        RuleFor(x => x.SeatId).NotEmpty();
        RuleFor(x => x.NewSeatTypeId).NotEmpty();
    }
}