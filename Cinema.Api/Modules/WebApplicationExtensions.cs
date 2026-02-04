using Cinema.Api.Hubs;
using Cinema.Infrastructure.Persistence;

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
        if (!app.Environment.IsDevelopment()) return;

        using var scope = app.Services.CreateScope();
        try 
        {
            var initialiser = scope.ServiceProvider.GetRequiredService<ApplicationDbContextInitializer>();
            await initialiser.InitialiseAsync();
            await initialiser.SeedAsync();
        }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
            logger.LogError(ex, "An error occurred during database initialisation.");
        }
    }
}