using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Cinema.OrdersTests;

public sealed class PostgresSchemaTemplate : IAsyncDisposable
{
    private readonly Lazy<Task<TemplateDatabase>> _template = new(CreateTemplateAsync);

    public async Task<(string ConnectionString, string AdminConnectionString, string DatabaseName)> CreateDatabaseAsync()
    {
        TemplateDatabase template = await _template.Value;
        string databaseName = $"cinema_orders_test_{Guid.NewGuid():N}";
        await using NpgsqlConnection admin = new(template.AdminConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand create = new($"CREATE DATABASE {databaseName} TEMPLATE {template.Name}", admin);
        await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        NpgsqlConnectionStringBuilder builder = new(template.ConnectionString)
        {
            Database = databaseName,
            Pooling = true
        };
        return (builder.ConnectionString, template.AdminConnectionString, databaseName);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_template.IsValueCreated || !_template.Value.IsCompletedSuccessfully)
            return;

        TemplateDatabase template = await _template.Value;
        await DropDatabaseAsync(template.AdminConnectionString, template.Name);
    }

    private static async Task<TemplateDatabase> CreateTemplateAsync()
    {
        string? connectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Skip("Set CINEMA_TEST_POSTGRES_CONNECTION to an isolated disposable pgvector database named cinema_orders_test_*.");

        NpgsqlConnectionStringBuilder builder = new(connectionString);
        if (builder.Database is null || !builder.Database.StartsWith("cinema_orders_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("PostgreSQL tests require a disposable cinema_orders_test_* database.");

        string databaseName = $"cinema_orders_test_template_{Guid.NewGuid():N}";
        builder.Database = "postgres";
        string adminConnectionString = builder.ConnectionString;
        await using (NpgsqlConnection admin = new(adminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE {databaseName}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            builder.Database = databaseName;
            builder.Pooling = false;
            string templateConnectionString = builder.ConnectionString;
            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(templateConnectionString, x => x.UseVector())
                .UseSnakeCaseNamingConvention().Options;
            await using ApplicationDbContext context = new(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new TemplateDatabase(templateConnectionString, adminConnectionString, databaseName);
        }
        catch
        {
            await DropDatabaseAsync(adminConnectionString, databaseName);
            throw;
        }
    }

    private static async Task DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using NpgsqlConnection admin = new(adminConnectionString);
        await admin.OpenAsync();
        await using NpgsqlCommand drop = new($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
    }

    private sealed record TemplateDatabase(string ConnectionString, string AdminConnectionString, string Name);
}
