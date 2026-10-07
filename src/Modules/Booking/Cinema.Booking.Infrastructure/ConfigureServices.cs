using Cinema.Application.Common.Interfaces;
using Cinema.Booking.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Booking.Infrastructure;

public static class ConfigureServices
{
    public static IServiceCollection AddBookingInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<ISeatLockingService, RedisSeatLockingService>();
        return services;
    }
}
