using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Auth.Commands.LoginUser;

public record LoginResponse(string AccessToken, string RefreshToken);
public record LoginUserCommand(string Email, string Password) : IRequest<Result<LoginResponse>>;

public class LoginUserCommandHandler(IIdentityService identityService) 
    : IRequestHandler<LoginUserCommand, Result<LoginResponse>>
{
    public async Task<Result<LoginResponse>> Handle(LoginUserCommand request, CancellationToken ct)
    {
        var result = await identityService.LoginAsync(request.Email, request.Password);

        if (result.IsFailure)
            return Result.Failure<LoginResponse>(result.Error);
        return Result.Success(new LoginResponse(result.Value.AccessToken, result.Value.RefreshToken));
    }
}