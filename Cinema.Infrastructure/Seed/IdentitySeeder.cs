using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Seed;

public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration config, ILogger logger)
    {
        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = sp.GetRequiredService<UserManager<User>>();

        string[] roles = ["User", "Admin"];
        foreach (var roleName in roles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
                if (!roleResult.Succeeded)
                {
                    var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                    logger.LogError("Failed to create role {Role}: {Errors}", roleName, errors);
                    throw new Exception($"Failed to create role {roleName}: {errors}");
                }
            }
        }
        logger.LogInformation("Roles ensured...");

        var adminEmail = config["SeedAdmin:Email"] ?? Environment.GetEnvironmentVariable("SeedAdmin__Email") ?? Environment.GetEnvironmentVariable("SEED_ADMIN__EMAIL");
        var adminPassword = config["SeedAdmin:Password"] ?? Environment.GetEnvironmentVariable("SeedAdmin__Password") ?? Environment.GetEnvironmentVariable("SEED_ADMIN__PASSWORD");

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            return;
        }

        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        if (adminUser == null)
        {
            adminUser = new User
            {
                UserName = adminEmail,
                Email = adminEmail,
                FirstName = "System",
                LastName = "Admin",
                EmailConfirmed = true
            };

            var createResult = await userManager.CreateAsync(adminUser, adminPassword);
            if (!createResult.Succeeded)
            {
                var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                logger.LogError("Failed to seed admin user: {Errors}", errors);
                throw new Exception($"Failed to seed admin user: {errors}");
            }
        }

        if (!await userManager.IsInRoleAsync(adminUser, "ADMIN"))
        {
            var roleResult = await userManager.AddToRoleAsync(adminUser, "ADMIN");
            if (!roleResult.Succeeded)
            {
                var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                logger.LogError("Failed to assign ADMIN role: {Errors}", errors);
                throw new Exception($"Failed to assign ADMIN role: {errors}");
            }
        }

        logger.LogInformation("Admin ensured...");
    }
}