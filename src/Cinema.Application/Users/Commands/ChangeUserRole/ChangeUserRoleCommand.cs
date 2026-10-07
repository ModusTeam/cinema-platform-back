using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Users.Commands.ChangeUserRole;

public record ChangeUserRoleCommand(Guid UserId, string RoleName) : IRequest<Result>;

public class ChangeUserRoleCommandHandler(IUserService userService) 
    : IRequestHandler<ChangeUserRoleCommand, Result>
{
    public async Task<Result> Handle(ChangeUserRoleCommand request, CancellationToken ct)
    {
        return await userService.ChangeRoleAsync(request.UserId, request.RoleName);
    }
}