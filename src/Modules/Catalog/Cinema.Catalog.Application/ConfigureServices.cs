using System.Reflection;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Movies.Services;
using FluentValidation;
using Mapster;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace Cinema.Catalog.Application;

public static class ConfigureServices
{
    public static IServiceCollection AddCatalogApplication(this IServiceCollection services)
    {
        Assembly assembly = typeof(ConfigureServices).Assembly;
        services.AddValidatorsFromAssembly(assembly);
        services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(assembly));
        TypeAdapterConfig.GlobalSettings.Scan(assembly);
        TypeAdapterConfig.GlobalSettings.Compile();
        services.AddScoped<IMovieTmdbSyncService, MovieTmdbSyncService>();
        return services;
    }
}
