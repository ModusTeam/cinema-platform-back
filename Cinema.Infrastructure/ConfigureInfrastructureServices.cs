using System.Text;
using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Settings;
using Cinema.Domain.Entities;
using Cinema.Domain.Interfaces;
using Cinema.Infrastructure.Messaging.Consumers;
using Cinema.Infrastructure.Options;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Refit;
using StackExchange.Redis;

namespace Cinema.Infrastructure;

public static class ConfigureInfrastructureServices
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddTransient<ITicketGenerator, QuestPdfTicketGenerator>()
            .AddPersistence(configuration)
            .AddAuthenticationAndIdentity(configuration)
            .AddMessaging(configuration)
            .AddBackgroundJobs(configuration)
            .AddExternalServices(configuration)
            .AddDomainServices();

        return services;
    }

    private static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var dbConnectionString = configuration.GetConnectionString("DefaultConnection");
        var redisConnectionString = configuration.GetConnectionString("RedisConnection");

        // Redis
        services.AddSingleton<IConnectionMultiplexer>(sp =>
        {
            var options = ConfigurationOptions.Parse(redisConnectionString!);
            options.AbortOnConnectFail = false;
            return ConnectionMultiplexer.Connect(options);
        });

        services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = redisConnectionString;
            options.InstanceName = "Cinema_";
        });

        // PostgreSQL DataSource
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(dbConnectionString);
        dataSourceBuilder.EnableDynamicJson();
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();
        services.AddSingleton(dataSource);

        // Interceptors & DB Context
        services.AddScoped<ApplicationDbContextInitializer>();

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {

            options.UseNpgsql(dataSource, builder =>
                {
                    builder.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                    builder.UseVector();
                    builder.EnableRetryOnFailure(maxRetryCount: 3);
                })
                .UseSnakeCaseNamingConvention();
        });

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());

        return services;
    }

    private static IServiceCollection AddAuthenticationAndIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<JwtSettings>()
            .Bind(configuration.GetSection(JwtSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Identity Core
        services.AddIdentityCore<User>(options =>
            {
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequiredLength = 6;
                options.User.RequireUniqueEmail = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();

        // JWT Auth
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                var jwtSettings = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
                                  ?? throw new InvalidOperationException("JwtSettings are not configured");

                options.SaveToken = true;
                options.RequireHttpsMetadata = false;
                options.TokenValidationParameters = new TokenValidationParameters
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

        return services;
    }

    private static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        // 1. Email Service
        services.Configure<SmtpSettings>(configuration.GetSection(SmtpSettings.SectionName));
        services.AddTransient<IEmailService, SmtpEmailService>();

        // 2. Frontend Settings
        services.AddOptions<FrontendSettings>()
            .Bind(configuration.GetSection(FrontendSettings.SectionName))
            .ValidateDataAnnotations();

        // 3. MassTransit (RabbitMQ)
        services.AddMassTransit(x =>
        {
            x.AddConsumer<TicketPurchasedConsumer>();

            x.UsingRabbitMq((context, cfg) =>
            {
                var rabbitHost = configuration["RabbitMq:Host"];
                var rabbitUser = configuration["RabbitMq:Username"];
                var rabbitPass = configuration["RabbitMq:Password"];
                var rabbitVHost = configuration["RabbitMq:VirtualHost"];

                if (string.IsNullOrEmpty(rabbitHost)) return;

                cfg.Host(rabbitHost, 5671, rabbitVHost ?? "/", h =>
                {
                    h.Username(rabbitUser);
                    h.Password(rabbitPass);

                    h.UseSsl(s =>
                    {
                        s.Protocol = System.Security.Authentication.SslProtocols.Tls12;
                    });
                });

                cfg.ConfigureEndpoints(context);
            });
        });

        return services;
    }

    private static IServiceCollection AddBackgroundJobs(this IServiceCollection services, IConfiguration configuration)
    {
        var dbConnectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddHangfire(config => config
            .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(dbConnectionString)));

        services.AddHangfireServer(options => { options.WorkerCount = 2; });

        return services;
    }

    private static IServiceCollection AddExternalServices(this IServiceCollection services, IConfiguration configuration)
    {
        // TMDB (Refit)
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

        // Gemini AI (Typed HttpClient)
        services.Configure<GeminiOptions>(configuration.GetSection(GeminiOptions.SectionName));

        services.AddHttpClient<IAiEmbeddingService, GeminiEmbeddingService>((sp, client) =>
        {
            var settings = sp.GetRequiredService<IOptions<GeminiOptions>>().Value;
            if (!string.IsNullOrEmpty(settings.BaseUrl))
            {
                client.BaseAddress = new Uri(settings.BaseUrl);
            }
        });

        return services;
    }

    private static IServiceCollection AddDomainServices(this IServiceCollection services)
    {
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
