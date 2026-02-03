using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Refit;
using StackExchange.Redis;

namespace Cinema.Infrastructure;

public static class ConfigureInfrastructureServices
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var dbConnectionString = configuration.GetConnectionString("DefaultConnection");
        var redisConnectionString = configuration.GetConnectionString("RedisConnection");
        
        services.AddSingleton<IConnectionMultiplexer>(sp => 
        {
            var configurationOptions = ConfigurationOptions.Parse(redisConnectionString!);
            configurationOptions.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(configurationOptions);
        });
        

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.UseNpgsql(dbConnectionString, builder => 
            { 
                builder.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                builder.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorCodesToAdd: null);
            });
        });
        
        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "Cinema_";
        });

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options =>
                options.UseNpgsqlConnection(dbConnectionString)));

        services.AddHangfireServer();

        var retryPolicy = Policy
            .HandleResult<HttpResponseMessage>(r => !r.IsSuccessStatusCode && (int)r.StatusCode >= 500)
            .WaitAndRetryAsync(3, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)));

        services.Configure<TmdbSettings>(configuration.GetSection(TmdbSettings.SectionName));

        services.AddRefitClient<ITmdbApi>()
            .ConfigureHttpClient((sp, c) => 
            {
                var settings = sp.GetRequiredService<IOptions<TmdbSettings>>().Value;
                c.BaseAddress = new Uri(settings.BaseUrl);
            })
            .AddPolicyHandler(retryPolicy);
        
        var refitSettings = new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                WriteIndented = true
            })
        };

        services.AddRefitClient<ITmdbApi>(refitSettings)
            .ConfigureHttpClient((sp, c) => 
            {
                var settings = sp.GetRequiredService<IOptions<TmdbSettings>>().Value;
                c.BaseAddress = new Uri(settings.BaseUrl);
            })
            .AddPolicyHandler(retryPolicy);
        
        services.AddTransient<ISeatLockingService, RedisSeatLockingService>();
        services.AddTransient<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddTransient<ITokenService, TokenService>();
        services.AddTransient<IIdentityService, IdentityService>();
        services.AddTransient<IUserService, UserService>();
        services.AddScoped<IMovieInfoProvider, EfMovieInfoProvider>();
        
        services.AddPersistenceServices(configuration);

        return services;
    }
}