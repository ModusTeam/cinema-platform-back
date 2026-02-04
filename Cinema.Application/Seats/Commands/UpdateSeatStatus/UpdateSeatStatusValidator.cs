using Cinema.Domain.Enums;
using FluentValidation;

namespace Cinema.Application.Seats.Commands.UpdateSeatStatus;

public class UpdateSeatStatusValidator : AbstractValidator<UpdateSeatStatusCommand>
{
    public UpdateSeatStatusValidator()
    {
        RuleFor(x => x.SeatId).NotEmpty();
        RuleFor(x => x.NewStatus).IsInEnum().WithMessage("Invalid status value.");
    }
}