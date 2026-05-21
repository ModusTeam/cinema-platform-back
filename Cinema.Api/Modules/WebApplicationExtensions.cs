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

            await app.SeedIdentityAsync(scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "An error occurred during database initialisation/seeding.");
            throw;
        }
    }

    public static async Task SeedIdentityAsync(this WebApplication app, IServiceProvider sp)
    {
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("IdentitySeeder");

        await IdentitySeeder.SeedAsync(sp, app.Configuration, logger);
    }
}