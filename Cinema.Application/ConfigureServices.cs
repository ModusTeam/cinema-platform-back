using System.Reflection;
using Cinema.Application.Common.Behaviours;
using Cinema.Application.Services;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Application;

public static class ConfigureServices
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();
        services.AddValidatorsFromAssembly(assembly);

        services.AddMediatR(cfg => 
        {
            cfg.RegisterServicesFromAssembly(assembly);
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        });
        
        services.AddScoped<SessionSchedulingService>();
        return services;
    }
}