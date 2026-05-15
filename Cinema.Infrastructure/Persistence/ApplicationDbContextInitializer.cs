using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cinema.Infrastructure.Persistence;

public class ApplicationDbContextInitializer
{
    private readonly ILogger<ApplicationDbContextInitializer> _logger;
    private readonly ApplicationDbContext _context;
    private readonly RoleManager<IdentityRole<Guid>> _roleManager;
    private readonly UserManager<User> _userManager;

    public ApplicationDbContextInitializer(
        ILogger<ApplicationDbContextInitializer> logger, 
        ApplicationDbContext context,
        UserManager<User> userManager,
        RoleManager<IdentityRole<Guid>> roleManager)
    {
        _logger = logger;
        _context = context;
        _roleManager = roleManager;
        _userManager = userManager;
    }

    public async Task InitialiseAsync()
    {
        try
        {
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.MigrateAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while initialising the database.");
            throw;
        }
    }

    public async Task SeedAsync()
    {
        try
        {
            await TrySeedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while seeding the database.");
            throw;
        }
    }

    public async Task TrySeedAsync()
    {
        // 1. Roles
        var roles = new[] { "Admin", "User" };
        foreach (var role in roles)
        {
            if (!await _roleManager.RoleExistsAsync(role))
            {
                await _roleManager.CreateAsync(new IdentityRole<Guid>(role));
            }
        }

        // 2. ДОДАЄМО СІДИНГ АДМІНІСТРАТОРА <-- ОСЬ ЦЕЙ БЛОК
        var adminEmail = "admin@cinema.com";
        var adminUser = await _userManager.FindByEmailAsync(adminEmail);

        if (adminUser == null)
        {
            _logger.LogInformation("Seeding Default Admin User...");
            
            var admin = new User
            {
                UserName = adminEmail,
                Email = adminEmail,
                FirstName = "System", // Заміни на свої властивості, якщо вони інші
                LastName = "Admin",
                EmailConfirmed = true // Щоб не вимагало підтвердження пошти при логіні
            };

            // Створюємо юзера з дефолтним паролем (Має містити велику літеру, цифру і спецсимвол)
            var result = await _userManager.CreateAsync(admin, "Admin123!");

            if (result.Succeeded)
            {
                // Додаємо йому роль
                await _userManager.AddToRoleAsync(admin, "Admin");
            }
            else
            {
                _logger.LogError("Failed to seed admin user: {Errors}", 
                    string.Join(", ", result.Errors.Select(e => e.Description)));
            }
        }
    }
}