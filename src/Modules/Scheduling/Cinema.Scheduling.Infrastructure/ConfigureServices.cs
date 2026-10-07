using Cinema.Domain.Interfaces;
using Cinema.Scheduling.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Scheduling.Infrastructure;

public static class ConfigureServices
{
    public static IServiceCollection AddSchedulingInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IMovieInfoProvider, EfMovieInfoProvider>();
        return services;
    }
}
