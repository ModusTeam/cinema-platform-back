using System.Reflection;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Booking.Application;

public static class ConfigureServices
{
    public static IServiceCollection AddBookingApplication(this IServiceCollection services)
    {
        Assembly assembly = typeof(ConfigureServices).Assembly;
        services.AddValidatorsFromAssembly(assembly);
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(assembly));
        return services;
    }
}
