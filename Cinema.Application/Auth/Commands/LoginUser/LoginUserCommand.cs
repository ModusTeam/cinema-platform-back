using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Auth.Commands.LoginUser;

public record LoginUserCommand(string Email, string Password) : IRequest<Result<string>>;

public class LoginUserCommandHandler(IIdentityService identityService) 
    : IRequestHandler<LoginUserCommand, Result<string>>
{
    public async Task<Result<string>> Handle(LoginUserCommand request, CancellationToken ct)
    {
        return await identityService.LoginAsync(request.Email, request.Password);
    }
}