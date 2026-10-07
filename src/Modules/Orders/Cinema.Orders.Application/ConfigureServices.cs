using System.Reflection;
using FluentValidation;
using Mapster;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Orders.Application;

public static class ConfigureServices
{
    public static IServiceCollection AddOrdersApplication(this IServiceCollection services)
    {
        Assembly assembly = typeof(ConfigureServices).Assembly;
        services.AddValidatorsFromAssembly(assembly);
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(assembly));
        TypeAdapterConfig.GlobalSettings.Scan(assembly);
        TypeAdapterConfig.GlobalSettings.Compile();
        return services;
    }
}
