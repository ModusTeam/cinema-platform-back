using Cinema.Api.Middleware;
using Cinema.Api.Modules;
using Hangfire;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) =>
    configuration.ReadFrom.Configuration(context.Configuration));

builder.Services.AddWebServices(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseSerilogRequestLogging();
app.UseMiddleware<RequestLogContextMiddleware>();
app.UseSwagger();
app.UseSwaggerUI();

app.UseHttpsRedirection();
app.UseCors("AllowAll");
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseOutputCache();
app.UseHangfireDashboard();

app.MapInfrastructureEndpoints();
app.MapControllers();

app.Run();