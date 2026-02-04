using System.Reflection;
using Cinema.Application.Common.Behaviours;
using Cinema.Application.Services;
using FluentValidation;
using Mapster;
using MapsterMapper;
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
            cfg.AddBehavior(typeof(IPipelineBehavior<,>), typeof(IdempotencyBehavior<,>));
        });
        
        var config = TypeAdapterConfig.GlobalSettings;

        config.Scan(assembly);

        config.Compile();
        
        services.AddSingleton(config);
        services.AddScoped<IMapper, ServiceMapper>();
        services.AddScoped<SessionSchedulingService>();
        return services;
    }
}