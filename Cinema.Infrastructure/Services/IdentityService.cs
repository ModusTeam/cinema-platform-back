using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Cinema.Infrastructure.Services;

public class IdentityService(
    UserManager<User> userManager,
    ApplicationDbContext context,
    IConfiguration configuration) : IIdentityService
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

        await userManager.AddToRoleAsync(user, "User");

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
        var accessToken = GenerateJwtToken(user, roles);
        var refreshToken = GenerateRefreshToken();
        
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
            return Result.Failure<(string, string)>(new Error("Token.Invalid", "Invalid token"));

        if (storedToken.ExpiryDate < DateTime.UtcNow)
            return Result.Failure<(string, string)>(new Error("Token.Expired", "Token expired"));

        var user = storedToken.User;
        var roles = await userManager.GetRolesAsync(user);
        
        var newAccessToken = GenerateJwtToken(user, roles);
        var newRefreshToken = GenerateRefreshToken();
        
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

    private string GenerateJwtToken(User user, IList<string> roles)
    {
        var jwtSettings = configuration.GetSection("JwtSettings");
        var secretKey = Encoding.UTF8.GetBytes(jwtSettings["Secret"]!);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new("firstName", user.FirstName ?? ""),
            new("lastName", user.LastName ?? "")
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = DateTime.UtcNow.AddMinutes(int.Parse(jwtSettings["ExpirationInMinutes"]!)),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secretKey), SecurityAlgorithms.HmacSha256Signature),
            Issuer = jwtSettings["Issuer"],
            Audience = jwtSettings["Audience"]
        };

        var tokenHandler = new JwtSecurityTokenHandler();
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }

    private static string GenerateRefreshToken()
    {
        var randomNumber = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }
}