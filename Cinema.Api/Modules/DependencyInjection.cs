using System.Reflection;
using System.Threading.RateLimiting;
using Cinema.Api.Services;
using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Infrastructure;
using Microsoft.OpenApi.Models;

namespace Cinema.Api.Modules;

public static class DependencyInjection
{
    public static IServiceCollection AddWebServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApplication();
        services.AddInfrastructureServices(configuration);

        var appSettings = configuration.Get<ApplicationSettings>();
        if (appSettings != null)
            services.AddSingleton(appSettings);

        services.AddControllers();
        services.AddHttpContextAccessor();
        
        services.AddCors(options =>
        {
            options.AddPolicy("AllowAll", policy =>
            {
                policy
                    .AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            });
        });
        
        services.AddOutputCache(options =>
        {
            options.AddBasePolicy(builder => builder.Expire(TimeSpan.FromSeconds(60)));
        });
        
        services.AddStackExchangeRedisOutputCache(options =>
        {
            options.Configuration = configuration.GetConnectionString("RedisConnection");
            options.InstanceName = "Cinema_OutputCache_";
        });

        services.AddMemoryCache();
        services.AddDataProtection();
        
        services.AddScoped<ICurrentUserService, CurrentUserService>();

        services.AddTransient<ITicketNotifier, SignalRTicketNotifier>();
        services.AddSignalR();

        services.AddRateLimiter(options =>
        {
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        AutoReplenishment = true,
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    }));

            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        });

        services.AddProblemDetails();
        services.AddEndpointsApiExplorer();
        services.AddSwaggerConfiguration();

        return services;
    }

    private static void AddSwaggerConfiguration(this IServiceCollection services)
    {
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Cinema API",
                Version = "v1",
                Description = "Cinema Platform API"
            });

            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Description = "JWT Authorization header using the Bearer scheme.",
                Name = "Authorization",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer"
            });

            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                {
                    new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference
                        {
                            Type = ReferenceType.SecurityScheme,
                            Id = "Bearer"
                        }
                    },
                    new List<string>()
                }
            });
        });
    }
}