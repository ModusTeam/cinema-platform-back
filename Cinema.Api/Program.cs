using Cinema.Api.Modules;
using Cinema.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebServices(builder.Configuration);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    using (var scope = app.Services.CreateScope())
    {
        {
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
    
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();