using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Seed;

public static class IdentitySeeder
{
    public static async Task SeedAsync(IServiceProvider sp, IConfiguration config, ILogger logger)
    {
        var seedRun = config["Seed:Run"] ?? 
                      config["SEED__RUN"] ?? 
                      Environment.GetEnvironmentVariable("SEED__RUN");
        var hostEnvironment = sp.GetRequiredService<IHostEnvironment>();
        
        bool isProduction = hostEnvironment.IsProduction();
        
        bool isSeedRunEnabled;
        if (!string.IsNullOrEmpty(seedRun))
        {
            isSeedRunEnabled = string.Equals(seedRun, "true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // Default to true in Development, false in Production/other environments
            isSeedRunEnabled = !isProduction;
        }

        if (!isSeedRunEnabled)
        {
            logger.LogInformation("Database seeding is disabled. Skipping.");
            return;
        }

        logger.LogInformation("Starting identity seeding process...");

        var roleManager = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var userManager = sp.GetRequiredService<UserManager<User>>();

        // Seed Roles
        string[] roles = ["User", "Admin"];
        foreach (var roleName in roles)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                logger.LogInformation("Creating role: {Role}", roleName);
                var roleResult = await roleManager.CreateAsync(new IdentityRole<Guid>(roleName));
                if (!roleResult.Succeeded)
                {
                    var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                    logger.LogError("Failed to create role {Role}: {Errors}", roleName, errors);
                    throw new Exception($"Failed to create role {roleName}: {errors}");
                }
            }
        }

        // Seed Admin User
        var adminEmail = config["SeedAdmin:Email"] ?? 
                         config["SEED_ADMIN__EMAIL"] ?? 
                         Environment.GetEnvironmentVariable("SEED_ADMIN__EMAIL");
        var adminPassword = config["SeedAdmin:Password"] ?? 
                            config["SEED_ADMIN__PASSWORD"] ?? 
                            Environment.GetEnvironmentVariable("SEED_ADMIN__PASSWORD");
        var firstName = config["SeedAdmin:FirstName"] ?? 
                        config["SEED_ADMIN__FIRSTNAME"] ?? 
                        Environment.GetEnvironmentVariable("SEED_ADMIN__FIRSTNAME") ?? 
                        "System";
        var lastName = config["SeedAdmin:LastName"] ?? 
                       config["SEED_ADMIN__LASTNAME"] ?? 
                       Environment.GetEnvironmentVariable("SEED_ADMIN__LASTNAME") ?? 
                       "Admin";

        if (string.IsNullOrWhiteSpace(adminEmail) || string.IsNullOrWhiteSpace(adminPassword))
        {
            logger.LogWarning("Seed admin credentials are not set (SEED_ADMIN__EMAIL / SEED_ADMIN__PASSWORD). Skipping admin user seeding.");
            return;
        }

        var adminUser = await userManager.FindByEmailAsync(adminEmail);
        if (adminUser == null)
        {
            logger.LogInformation("Seeding default admin user: {Email}", adminEmail);
            adminUser = new User
            {
                UserName = adminEmail,
                Email = adminEmail,
                FirstName = firstName,
                LastName = lastName,
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

        if (!await userManager.IsInRoleAsync(adminUser, "Admin"))
        {
            logger.LogInformation("Assigning Admin role to user: {Email}", adminEmail);
            var roleResult = await userManager.AddToRoleAsync(adminUser, "Admin");
            if (!roleResult.Succeeded)
            {
                var errors = string.Join(", ", roleResult.Errors.Select(e => e.Description));
                logger.LogError("Failed to assign Admin role: {Errors}", errors);
                throw new Exception($"Failed to assign Admin role: {errors}");
            }
        }

        logger.LogInformation("Identity seeding completed successfully.");
    }
}