using Cinema.Api.Middleware;
using Cinema.Api.Modules;
using Hangfire;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddWebServices(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await app.InitialiseDatabaseAsync();
}

await app.SeedIdentityIfEnabledAsync();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseMiddleware<RequestLogContextMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

if (app.Environment.IsProduction() == false)
{
    app.UseHttpsRedirection();
}
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