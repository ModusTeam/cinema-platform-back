using System.Text;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;
using Cinema.Infrastructure.Authentication;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Persistence.Interceptors; // Переконайся, що папка створена
using Cinema.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
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
        
        var jwtSettings = new JwtSettings();
        configuration.Bind(JwtSettings.SectionName, jwtSettings);
        services.AddSingleton(Options.Create(jwtSettings));
        services.Configure<JwtSettings>(configuration.GetSection(JwtSettings.SectionName));
        
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.SaveToken = true;
                options.RequireHttpsMetadata = false; 
                options.TokenValidationParameters = new TokenValidationParameters()
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtSettings.Issuer,
                    ValidAudience = jwtSettings.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Secret))
                };
            });
        
        services.AddAuthorization();
        
        services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();
        
        var dataSourceBuilder = new Npgsql.NpgsqlDataSourceBuilder(dbConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            var interceptor = sp.GetRequiredService<ISaveChangesInterceptor>();

            options.UseNpgsql(dataSource, builder => 
            { 
                builder.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                builder.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorCodesToAdd: null);
            })
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptor);
        });
        
        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());
        services.AddScoped<ApplicationDbContextInitializer>();
        
        services.AddIdentityCore<User>(options => 
            {
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequiredLength = 6;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();
        
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

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = 2;
        });
        
        services.Configure<TmdbSettings>(configuration.GetSection(TmdbSettings.SectionName));

        var refitSettings = new RefitSettings
        {
            ContentSerializer = new SystemTextJsonContentSerializer(new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            })
        };
        
        services.AddRefitClient<ITmdbApi>(refitSettings)
            .ConfigureHttpClient((sp, c) => 
            {
                var settings = sp.GetRequiredService<IOptions<TmdbSettings>>().Value;
                c.BaseAddress = new Uri(settings.BaseUrl);
            })
            .AddStandardResilienceHandler();
        
        services.AddTransient<IPaymentService, MockPaymentService>();
        services.AddSingleton<ISeatTypeProvider, SeatTypeProvider>();
        services.AddTransient<IPriceCalculator, PriceCalculator>();
        services.AddSingleton<ISeatLockingService, RedisSeatLockingService>();
        services.AddTransient<IPasswordHasher<User>, PasswordHasher<User>>();
        services.AddTransient<ITokenService, TokenService>();
        services.AddTransient<IIdentityService, IdentityService>();
        services.AddTransient<IUserService, UserService>();
        services.AddScoped<IMovieInfoProvider, EfMovieInfoProvider>();

        return services;
    }
}