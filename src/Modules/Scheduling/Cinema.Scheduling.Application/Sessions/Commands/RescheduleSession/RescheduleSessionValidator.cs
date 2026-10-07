using FluentValidation;

namespace Cinema.Application.Sessions.Commands.RescheduleSession;


public class RescheduleSessionValidator : AbstractValidator<RescheduleSessionCommand>
{
    public RescheduleSessionValidator()
    {
        RuleFor(x => x.SessionId).NotEmpty();
        RuleFor(x => x.NewStartTime)
            .GreaterThan(DateTime.UtcNow).WithMessage("New session time must be in the future.");
    }
}