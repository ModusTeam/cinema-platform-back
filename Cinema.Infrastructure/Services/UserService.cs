using Cinema.Application.Common.Interfaces;
using Cinema.Application.Users.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Services;

public class UserService(
    UserManager<User> userManager,
    RoleManager<IdentityRole<Guid>> roleManager) : IUserService
{
    public async Task<Result<List<UserDto>>> GetAllAsync(CancellationToken ct = default)
    {
        var users = await userManager.Users.AsNoTracking().ToListAsync(ct);
        var userDtos = new List<UserDto>();

        foreach (var user in users)
        {
            var roles = await userManager.GetRolesAsync(user);
            var mainRole = roles.FirstOrDefault() ?? "None";
            
            userDtos.Add(new UserDto(user.Id, user.Email!, user.FirstName, user.LastName, mainRole));
        }

        return Result.Success(userDtos);
    }

    public async Task<Result> ChangeRoleAsync(Guid userId, string newRole)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user == null)
            return Result.Failure(new Error("User.NotFound", "User not found."));

        if (!await roleManager.RoleExistsAsync(newRole))
            return Result.Failure(new Error("Role.Invalid", $"Role '{newRole}' does not exist."));

        var currentRoles = await userManager.GetRolesAsync(user);
        if (currentRoles.Contains(newRole))
            return Result.Success();

        var removeResult = await userManager.RemoveFromRolesAsync(user, currentRoles);
        if (!removeResult.Succeeded)
            return Result.Failure(new Error("Identity.Error", "Failed to remove existing roles."));

        var addResult = await userManager.AddToRoleAsync(user, newRole);
        if (!addResult.Succeeded)
            return Result.Failure(new Error("Identity.Error", "Failed to assign new role."));

        return Result.Success();
    }
    
    
}