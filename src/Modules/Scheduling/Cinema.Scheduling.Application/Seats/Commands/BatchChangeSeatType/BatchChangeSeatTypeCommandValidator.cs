using FluentValidation;

namespace Cinema.Application.Seats.Commands.BatchChangeSeatType;

public class BatchChangeSeatTypeCommandValidator : AbstractValidator<BatchChangeSeatTypeCommand>
{
    public BatchChangeSeatTypeCommandValidator()
    {
        RuleFor(x => x.HallId).NotEmpty();
        RuleFor(x => x.NewSeatTypeId).NotEmpty();
        RuleFor(x => x.SeatIds).NotEmpty();
        RuleForEach(x => x.SeatIds).NotEmpty();
    }
}
