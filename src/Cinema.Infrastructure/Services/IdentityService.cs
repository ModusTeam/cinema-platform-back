using Cinema.Application.Common.Constants;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Services;

public class IdentityService(
    UserManager<User> userManager,
    ITokenService tokenService,
    ApplicationDbContext context) : IIdentityService
{
    public async Task<Result<Guid>> RegisterAsync(string email, string password, string firstName, string lastName)
    {
        var user = new User
        {
            UserName = email,
            Email = email,
            FirstName = firstName,
            LastName = lastName
        };

        var result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            return Result.Failure<Guid>(new Error("Identity.RegisterFailed", errors));
        }

        await userManager.AddToRoleAsync(user, Roles.User);

        return Result.Success(user.Id);
    }

    public async Task<Result<(string AccessToken, string RefreshToken)>> LoginAsync(string email, string password)
    {
        var user = await userManager.FindByEmailAsync(email);
        if (user == null)
            return Result.Failure<(string, string)>(new Error("Identity.LoginFailed", "Invalid email or password."));

        var checkPassword = await userManager.CheckPasswordAsync(user, password);
        if (!checkPassword)
            return Result.Failure<(string, string)>(new Error("Identity.LoginFailed", "Invalid email or password."));
        
        var roles = await userManager.GetRolesAsync(user);
        var accessToken = tokenService.GenerateAccessToken(user, roles);
        var refreshToken = tokenService.GenerateRefreshToken();
        
        var refreshTokenEntity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = refreshToken,
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.AddDays(7),
            IsRevoked = false
        };

        await context.RefreshTokens.AddAsync(refreshTokenEntity);
        await context.SaveChangesAsync();

        return Result.Success((accessToken, refreshToken));
    }

    public async Task<Result<(string AccessToken, string RefreshToken)>> RefreshTokenAsync(string requestRefreshToken)
    {
        var storedToken = await context.RefreshTokens
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.Token == requestRefreshToken);

        if (storedToken == null || storedToken.IsRevoked)
            return Result.Failure<(string, string)>(new Error("Token.Invalid", "Invalid or revoked token."));

        if (storedToken.ExpiryDate < DateTime.UtcNow)
            return Result.Failure<(string, string)>(new Error("Token.Expired", "Token has expired."));

        var user = storedToken.User;
        var roles = await userManager.GetRolesAsync(user);
        
        var newAccessToken = tokenService.GenerateAccessToken(user, roles);
        var newRefreshToken = tokenService.GenerateRefreshToken();

        storedToken.IsRevoked = true;

        var newTokenEntity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            Token = newRefreshToken,
            UserId = user.Id,
            ExpiryDate = DateTime.UtcNow.AddDays(7)
        };

        await context.RefreshTokens.AddAsync(newTokenEntity);
        await context.SaveChangesAsync();

        return Result.Success((newAccessToken, newRefreshToken));
    }
}