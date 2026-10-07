using System.Reflection;
using Cinema.Application.Services;
using Cinema.Domain.Services;
using FluentValidation;
using Mapster;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Scheduling.Application;

public static class ConfigureServices
{
    public static IServiceCollection AddSchedulingApplication(this IServiceCollection services)
    {
        Assembly assembly = typeof(ConfigureServices).Assembly;
        services.AddValidatorsFromAssembly(assembly);
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(assembly));
        TypeAdapterConfig.GlobalSettings.Scan(assembly);
        TypeAdapterConfig.GlobalSettings.Compile();
        services.AddScoped<SessionSchedulingService>();
        services.AddScoped<SeatLayoutService>();
        return services;
    }
}
