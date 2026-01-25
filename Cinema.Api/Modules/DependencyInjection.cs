using Cinema.Application;
using Cinema.Application.Common.Settings;
using Cinema.Infrastructure;
using Microsoft.OpenApi.Models;

namespace Cinema.Api.Modules;

public static class DependencyInjection
{
    public static IServiceCollection AddWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApplication();
        services.AddInfrastructureServices(configuration);

        var appSettings = configuration.Get<ApplicationSettings>();
        if (appSettings != null)
        {
            services.AddSingleton(appSettings);
        }

        services.AddControllers();
        
        services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
                policy.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader());
        });
        
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo 
            { 
                Title = "Cinema API", 
                Version = "v1",
                Description = "test api for the cinema project"
            });
        });

        return services;
    }
}