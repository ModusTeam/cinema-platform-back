using System.Diagnostics;
using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Cinema.DbUpdaterTests;

public class DbUpdaterCliTests
{
    [Fact]
    public async Task Help_Explains_Commands_Without_Connecting()
    {
        ProcessResult result = await RunAsync(["--help"], connectionString: "");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--check", result.Output);
        Assert.Contains("--seed", result.Output);
        Assert.DoesNotContain("Connecting to PostgreSQL", result.Output);
    }

    [Theory]
    [InlineData("--unexpected")]
    [InlineData("--check", "--seed")]
    [InlineData("--help", "--seed")]
    public async Task Unknown_Or_Combined_Arguments_Are_Rejected(params string[] arguments)
    {
        ProcessResult result = await RunAsync(arguments, connectionString: "");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Invalid arguments", result.Output);
        Assert.DoesNotContain("Connecting to PostgreSQL", result.Output);
    }

    [Theory]
    [InlineData()]
    [InlineData("--check")]
    [InlineData("--seed")]
    public async Task Valid_Commands_Require_A_Connection_String(params string[] arguments)
    {
        ProcessResult result = await RunAsync(arguments, connectionString: "");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Missing ConnectionStrings:DefaultConnection", result.Output);
        Assert.DoesNotContain("Invalid arguments", result.Output);
    }

    [Fact]
    public async Task Invalid_Configuration_Does_Not_Log_Connection_String()
    {
        const string connectionString = "Host=localhost;Database=cinema_test;Password=private-test-password;NotAKeyword=true";
        ProcessResult result = await RunAsync([], connectionString);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("ConnectionStrings:DefaultConnection is invalid", result.Output);
        Assert.DoesNotContain(connectionString, result.Output);
        Assert.DoesNotContain("private-test-password", result.Output);
    }

    [Fact]
    public async Task Development_User_Secrets_Are_Loaded_And_Environment_Variables_Override_Them()
    {
        string root = Path.Combine(Path.GetTempPath(), $"cinema-dbupdater-secrets-{Guid.NewGuid():N}");
        string secretsDirectory = OperatingSystem.IsWindows()
            ? Path.Combine(root, "Microsoft", "UserSecrets", "cinema-db-updater-local")
            : Path.Combine(root, ".microsoft", "usersecrets", "cinema-db-updater-local");
        Directory.CreateDirectory(secretsDirectory);
        await File.WriteAllTextAsync(Path.Combine(secretsDirectory, "secrets.json"),
            "{\"ConnectionStrings\":{\"DefaultConnection\":\"Host=localhost;Database=\"}}",
            TestContext.Current.CancellationToken);

        try
        {
            ProcessResult fromSecret = await RunAsync([], null, environment: "Development", userSecretsRoot: root);
            Assert.Equal(2, fromSecret.ExitCode);
            Assert.Contains("must specify Host and Database", fromSecret.Output);

            ProcessResult overridden = await RunAsync([], "NotAConnectionString", environment: "Development",
                userSecretsRoot: root);
            Assert.Equal(2, overridden.ExitCode);
            Assert.Contains("connection string format", overridden.Output);
            Assert.DoesNotContain("must specify Host and Database", overridden.Output);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Migration_Lifecycle_Preserves_Data_And_Check_Mode_Is_Read_Only()
    {
        string? testConnectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(testConnectionString))
            Assert.Skip("Set CINEMA_TEST_POSTGRES_CONNECTION to an isolated pgvector database named cinema_orders_test_*.");

        NpgsqlConnectionStringBuilder builder = new(testConnectionString);
        if (builder.Database is null || !builder.Database.StartsWith("cinema_orders_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Updater tests require an isolated cinema_orders_test_* base database.");

        string databaseName = $"cinema_dbupdater_test_{Guid.NewGuid():N}";
        builder.Database = "postgres";
        string adminConnectionString = builder.ConnectionString;
        await using (NpgsqlConnection admin = new(adminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE {databaseName}", admin);
            create.CommandTimeout = 180;
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        builder.Database = databaseName;
        string connectionString = builder.ConnectionString;

        try
        {
            ProcessResult initialCheck = await RunAsync(["--check"], connectionString, Path.GetTempPath());
            Assert.Equal(1, initialCheck.ExitCode);
            Assert.Contains("Applied migrations: 0", initialCheck.Output);
            Assert.Matches("Pending migrations: [1-9][0-9]*", initialCheck.Output);
            Assert.Equal(0, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'"));

            ProcessResult firstRun = await RunAsync([], connectionString);
            Assert.Equal(0, firstRun.ExitCode);
            Assert.Contains("Migrations applied successfully", firstRun.Output);

            await using NpgsqlDataSource dataSource = DatabaseContextConfiguration.CreateDataSource(connectionString);
            DbContextOptionsBuilder<ApplicationDbContext> options = new();
            DatabaseContextConfiguration.Configure(options, dataSource);
            await using ApplicationDbContext context = new(options.Options);
            int migrationCount = context.Database.GetMigrations().Count();

            Assert.True(migrationCount > 0);
            Assert.Equal(migrationCount, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM \"__EFMigrationsHistory\""));
            Assert.Equal(2, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('movies', 'genres')"));

            Guid markerId = Guid.NewGuid();
            await using (NpgsqlConnection connection = new(connectionString))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using NpgsqlCommand insert = new(
                    "INSERT INTO genres (id, name, slug) VALUES (@id, @name, @slug)", connection);
                insert.Parameters.AddWithValue("id", markerId);
                insert.Parameters.AddWithValue("name", "DbUpdater verification");
                insert.Parameters.AddWithValue("slug", "dbupdater-verification");
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            ProcessResult repeatedRun = await RunAsync([], connectionString);
            Assert.Equal(0, repeatedRun.ExitCode);
            Assert.Contains("Pending migrations: 0", repeatedRun.Output);
            Assert.DoesNotContain("Applying migrations", repeatedRun.Output);
            Assert.Equal("DbUpdater verification:dbupdater-verification", await ScalarAsync<string>(connectionString,
                "SELECT name || ':' || slug FROM genres WHERE id = @id", markerId));

            int tablesBeforeSeed = await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'");
            int rowsBeforeSeed = await ScalarAsync<int>(connectionString,
                "SELECT (SELECT count(*) FROM genres) + (SELECT count(*) FROM movies) + " +
                "(SELECT count(*) FROM \"AspNetUsers\") + (SELECT count(*) FROM \"AspNetRoles\")");
            ProcessResult seedRun = await RunAsync(["--seed"], connectionString);
            Assert.Equal(0, seedRun.ExitCode);
            Assert.Contains("No seed operations are configured", seedRun.Output);
            Assert.Equal(tablesBeforeSeed, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM pg_tables WHERE schemaname = 'public'"));
            Assert.Equal(rowsBeforeSeed, await ScalarAsync<int>(connectionString,
                "SELECT (SELECT count(*) FROM genres) + (SELECT count(*) FROM movies) + " +
                "(SELECT count(*) FROM \"AspNetUsers\") + (SELECT count(*) FROM \"AspNetRoles\")"));
            Assert.Equal(1, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM genres WHERE id = @id", markerId));
            Assert.Equal("DbUpdater verification:dbupdater-verification", await ScalarAsync<string>(connectionString,
                "SELECT name || ':' || slug FROM genres WHERE id = @id", markerId));
            Assert.Equal(migrationCount, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM \"__EFMigrationsHistory\""));

            ProcessResult finalCheck = await RunAsync(["--check"], connectionString);
            Assert.Equal(0, finalCheck.ExitCode);
            Assert.Contains($"Applied migrations: {migrationCount}", finalCheck.Output);
            Assert.Contains("Pending migrations: 0", finalCheck.Output);
            Assert.Equal(1, await ScalarAsync<int>(connectionString,
                "SELECT count(*) FROM genres WHERE id = @id", markerId));
        }
        finally
        {
            await using NpgsqlConnection admin = new(adminConnectionString);
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand drop = new($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
            drop.CommandTimeout = 180;
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql, Guid? id = null)
    {
        await using NpgsqlConnection connection = new(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlCommand command = new(sql, connection);
        if (id is not null)
            command.Parameters.AddWithValue("id", id.Value);
        object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return (T)Convert.ChangeType(value!, typeof(T));
    }

    private static async Task<ProcessResult> RunAsync(string[] arguments, string? connectionString,
        string? workingDirectory = null, string environment = "Production", string? userSecretsRoot = null)
    {
        string updaterPath = Path.Combine(AppContext.BaseDirectory, "Cinema.DbUpdater.dll");
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add(updaterPath);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["DOTNET_ENVIRONMENT"] = environment;
        if (connectionString is null)
            startInfo.Environment.Remove("ConnectionStrings__DefaultConnection");
        else
            startInfo.Environment["ConnectionStrings__DefaultConnection"] = connectionString;
        if (userSecretsRoot is not null)
        {
            if (OperatingSystem.IsWindows())
                startInfo.Environment["APPDATA"] = userSecretsRoot;
            else
            {
                startInfo.Environment.Remove("APPDATA");
                startInfo.Environment["HOME"] = userSecretsRoot;
            }
        }

        using Process process = Process.Start(startInfo)!;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return new ProcessResult(process.ExitCode, await output + await errors);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw;
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
