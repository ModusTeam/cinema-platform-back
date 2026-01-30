using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Auth.Commands.RefreshToken;

public record RefreshTokenCommand(string Token) : IRequest<Result<RefreshTokenResponse>>;

public record RefreshTokenResponse(string AccessToken, string RefreshToken);

public class RefreshTokenCommandHandler(IIdentityService identityService) 
    : IRequestHandler<RefreshTokenCommand, Result<RefreshTokenResponse>>
{
    public async Task<Result<RefreshTokenResponse>> Handle(RefreshTokenCommand request, CancellationToken ct)
    {
        var result = await identityService.RefreshTokenAsync(request.Token);

        if (result.IsFailure)
            return Result.Failure<RefreshTokenResponse>(result.Error);
        
        return Result.Success(new RefreshTokenResponse(result.Value.AccessToken, result.Value.RefreshToken));
    }
}