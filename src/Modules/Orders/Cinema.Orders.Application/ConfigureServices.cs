using System.Reflection;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Services;
using Cinema.Application.Services;
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
        services.AddScoped<IOrderReservationService, OrderReservationService>();
        services.AddScoped<IOrderCheckoutOrchestrator, OrderCheckoutOrchestrator>();
        services.AddScoped<IGoldUpgradePricingService, GoldUpgradePricingService>();
        return services;
    }
}
