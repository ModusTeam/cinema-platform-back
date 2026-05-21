using Cinema.Api.Middleware;
using Cinema.Api.Modules;
using Hangfire;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddWebServices(builder.Configuration);

var app = builder.Build();

// Seed mode detection (either via CLI argument '--seed' or SEED__MODE environment variable)
var isSeedMode = args.Contains("--seed") || 
                 string.Equals(app.Configuration["SEED__MODE"], "true", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(app.Configuration["Seed__Mode"], "true", StringComparison.OrdinalIgnoreCase);

if (isSeedMode)
{
    using (var scope = app.Services.CreateScope())
    {
        var sp = scope.ServiceProvider;
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("SeederCommandLine");
        try
        {
            logger.LogInformation("CLI/Env seed mode detected. Running identity seeding...");
            await Cinema.Infrastructure.Seed.IdentitySeeder.SeedAsync(sp, app.Configuration, logger);
            logger.LogInformation("Identity seeding completed successfully.");
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "An error occurred during CLI/Env identity seeding.");
            Environment.Exit(1);
        }
    }
}

if (app.Environment.IsDevelopment())
{
    await app.InitialiseDatabaseAsync();
}

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseMiddleware<RequestLogContextMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors("AllowClient");
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();
app.MapHub<Cinema.Api.Hubs.TicketHub>("/tickets"); 
app.UseOutputCache();
app.UseHangfireDashboard();

app.MapInfrastructureEndpoints();
app.MapControllers();

app.Run();