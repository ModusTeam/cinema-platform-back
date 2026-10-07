using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Auth.Commands.RegisterUser;

public record RegisterUserCommand(string Email, string Password, string FirstName, string LastName) 
    : IRequest<Result<Guid>>;

public class RegisterUserCommandHandler(IIdentityService identityService) 
    : IRequestHandler<RegisterUserCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(RegisterUserCommand request, CancellationToken ct)
    {
        return await identityService.RegisterAsync(request.Email, request.Password, request.FirstName, request.LastName);
    }
}