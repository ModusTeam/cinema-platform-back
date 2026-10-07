using FluentValidation;

namespace Cinema.Application.Halls.Commands.DeleteHall;

public class DeleteHallCommandValidator : AbstractValidator<DeleteHallCommand>
{
    public DeleteHallCommandValidator()
    {
        RuleFor(x => x.HallId).NotEmpty();
    }
}