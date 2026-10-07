using Cinema.Domain.Entities;

namespace Cinema.Application.Common.Interfaces;

public interface ITokenService
{
    string GenerateAccessToken(User user, IList<string> roles);
    string GenerateRefreshToken();
}