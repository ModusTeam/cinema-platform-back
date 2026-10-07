using Cinema.Api.Hubs;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Seed;

namespace Cinema.Api.Modules;

public static class WebApplicationExtensions
{
    public static WebApplication MapInfrastructureEndpoints(this WebApplication app)
    {
        app.MapHub<TicketHub>("/hubs/tickets");
        return app;
    }

    public static async Task InitialiseDatabaseAsync(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("DatabaseInitialisation");

        try
        {
            var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>();

            await initialiser.InitialiseAsync();
            await initialiser.SeedAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during database initialisation/seeding.");
            throw;
        }
    }

    public static async Task SeedIdentityIfEnabledAsync(this WebApplication app)
    {
        var enabledStr = app.Configuration["SeedIdentity:Enabled"] 
                         ?? Environment.GetEnvironmentVariable("SeedIdentity__Enabled")
                         ?? Environment.GetEnvironmentVariable("SEED_IDENTITY__ENABLED");
                         
        if (!string.Equals(enabledStr, "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

        try
        {
            logger.LogInformation("Identity seeding enabled, starting...");
            await IdentitySeeder.SeedAsync(scope.ServiceProvider, app.Configuration, logger);
            logger.LogInformation("Identity seeding completed.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during identity seeding.");
            throw;
        }
    }
}