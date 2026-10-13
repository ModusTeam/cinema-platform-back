using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cinema.Infrastructure.Persistence;

public static class DatabaseContextConfiguration
{
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableDynamicJson();
        builder.UseVector();
        return builder.Build();
    }

    public static void Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource)
    {
        options.UseNpgsql(dataSource, postgres =>
            {
                postgres.MigrationsAssembly(typeof(ApplicationDbContext).Assembly.FullName);
                postgres.UseVector();
                postgres.EnableRetryOnFailure(maxRetryCount: 3);
            })
            .UseSnakeCaseNamingConvention();
    }
}
