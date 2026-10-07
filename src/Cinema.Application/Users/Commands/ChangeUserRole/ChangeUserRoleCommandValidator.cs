using Cinema.Application.Common.Constants;
using FluentValidation;

namespace Cinema.Application.Users.Commands.ChangeUserRole;

public class ChangeUserRoleCommandValidator : AbstractValidator<ChangeUserRoleCommand>
{
    public ChangeUserRoleCommandValidator()
    {
        RuleFor(x => x.UserId).NotEmpty();
        RuleFor(x => x.RoleName)
            .NotEmpty()
            .Must(r => r == Roles.Admin || r == Roles.User)
            .WithMessage($"Role must be either '{Roles.Admin}' or '{Roles.User}'.");
    }
}