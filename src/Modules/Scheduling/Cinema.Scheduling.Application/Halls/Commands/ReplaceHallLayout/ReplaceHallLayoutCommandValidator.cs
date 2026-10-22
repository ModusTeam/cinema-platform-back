using FluentValidation;

namespace Cinema.Application.Halls.Commands.ReplaceHallLayout;

public class ReplaceHallLayoutCommandValidator : AbstractValidator<ReplaceHallLayoutCommand>
{
    public ReplaceHallLayoutCommandValidator()
    {
        RuleFor(x => x.HallId).NotEmpty();
        RuleFor(x => x.Seats).NotNull();
        When(x => x.Seats is not null, () =>
            RuleFor(x => new SparseHallLayout(x.Rows, x.SeatsPerRow, x.Seats))
                .SetValidator(new SparseHallLayoutValidator()).OverridePropertyName(string.Empty));
    }
}
