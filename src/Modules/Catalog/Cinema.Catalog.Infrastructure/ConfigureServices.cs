using System.Text.Json;
using Cinema.Catalog.Application.Common.Interfaces;
using Cinema.Catalog.Application.Common.Settings;
using Cinema.Catalog.Infrastructure.Options;
using Cinema.Catalog.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Refit;

namespace Cinema.Catalog.Infrastructure;

public static class ConfigureServices
{
    public static IServiceCollection AddCatalogInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TmdbSettings>()
            .Bind(configuration.GetSection(TmdbSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        RefitSettings tmdbRefitSettings = new()
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            })
        };
        services.AddRefitClient<ITmdbApi>(tmdbRefitSettings)
            .ConfigureHttpClient((sp, client) =>
            {
                TmdbSettings settings = sp.GetRequiredService<IOptions<TmdbSettings>>().Value;
                client.BaseAddress = new Uri(settings.BaseUrl);
            })
            .AddStandardResilienceHandler();
        services.AddScoped<ITmdbService, TmdbService>();

        services.AddOptions<GeminiOptions>()
            .Bind(configuration.GetSection(GeminiOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        RefitSettings geminiRefitSettings = new()
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            })
        };
        services.AddRefitClient<IGeminiApi>(geminiRefitSettings)
            .ConfigureHttpClient((sp, client) =>
            {
                GeminiOptions settings = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
                client.BaseAddress = new Uri(settings.BaseUrl);
            })
            .AddStandardResilienceHandler();
        services.AddScoped<IAiEmbeddingService, GeminiEmbeddingService>();
        return services;
    }
}
