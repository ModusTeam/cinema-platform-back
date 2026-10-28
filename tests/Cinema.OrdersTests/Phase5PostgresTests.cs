using System.Data;
using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class Phase5PostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    [Theory]
    [InlineData(IsolationLevel.ReadCommitted, "read committed")]
    [InlineData(IsolationLevel.Serializable, "serializable")]
    public async Task Requested_Isolation_Level_Reaches_Postgres(IsolationLevel level, string expected)
    {
        (string connectionString, string adminConnectionString, string databaseName) = await template.CreateDatabaseAsync();
        try
        {
            await using ApplicationDbContext context = CreateContext(connectionString);
            await using var transaction = await context.BeginTransactionAsync(level, TestContext.Current.CancellationToken);
            string actual = await context.Database.SqlQueryRaw<string>(
                "SELECT current_setting('transaction_isolation') AS \"Value\"")
                .SingleAsync(TestContext.Current.CancellationToken);
            actual.Should().Be(expected);
        }
        finally
        {
            await DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    [Fact]
    public async Task Concurrent_Refresh_Claims_One_Token_And_Creates_One_Successor()
    {
        (string connectionString, string adminConnectionString, string databaseName) = await template.CreateDatabaseAsync();
        try
        {
            Guid userId = Guid.NewGuid();
            string token = Guid.NewGuid().ToString();
            await using (ApplicationDbContext seed = CreateContext(connectionString))
            {
                seed.Users.Add(new User { Id = userId, UserName = $"user-{userId:N}" });
                seed.RefreshTokens.Add(new RefreshToken
                {
                    Id = Guid.NewGuid(), Token = token, UserId = userId,
                    ExpiryDate = DateTime.UtcNow.AddDays(1)
                });
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            await using ServiceProvider services = CreateServices(connectionString);
            using IServiceScope firstScope = services.CreateScope();
            using IServiceScope secondScope = services.CreateScope();
            IdentityService first = firstScope.ServiceProvider.GetRequiredService<IdentityService>();
            IdentityService second = secondScope.ServiceProvider.GetRequiredService<IdentityService>();

            var results = await Task.WhenAll(first.RefreshTokenAsync(token), second.RefreshTokenAsync(token));

            results.Count(result => result.IsSuccess).Should().Be(1);
            results.Single(result => result.IsFailure).Error.Code.Should().Be("Token.Invalid");
            await using ApplicationDbContext verify = CreateContext(connectionString);
            (await verify.RefreshTokens.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
            (await verify.RefreshTokens.CountAsync(x => !x.IsRevoked,
                TestContext.Current.CancellationToken)).Should().Be(1);
        }
        finally
        {
            await DropDatabaseAsync(adminConnectionString, databaseName);
        }
    }

    private static ServiceProvider CreateServices(string connectionString)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(connectionString, x => x.UseVector())
            .UseSnakeCaseNamingConvention());
        services.AddIdentityCore<User>().AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ApplicationDbContext>();
        ITokenService tokens = NSubstitute.Substitute.For<ITokenService>();
        tokens.GenerateAccessToken(Arg.Any<User>(), Arg.Any<IList<string>>()).Returns("access");
        tokens.GenerateRefreshToken().Returns(_ => Guid.NewGuid().ToString());
        services.AddSingleton(tokens);
        services.AddScoped<IdentityService>();
        services.AddScoped<CustomerRegistrationService>();
        services.AddScoped<CustomerFactsWriter>();
        services.AddSingleton(NSubstitute.Substitute.For<MassTransit.ISendEndpointProvider>());
        return services.BuildServiceProvider();
    }

    private static ApplicationDbContext CreateContext(string connectionString)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(connectionString, x => x.UseVector()).UseSnakeCaseNamingConvention().Options;
        return new ApplicationDbContext(options);
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }
}
