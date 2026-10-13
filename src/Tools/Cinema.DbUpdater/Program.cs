using Cinema.DbUpdater;
using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

const string usage = "Usage: Cinema.DbUpdater [--check | --seed | --help]";

if (args.Length == 1 && args[0] == "--help")
{
    Console.WriteLine(usage);
    Console.WriteLine("  (no flag)  Apply pending migrations.");
    Console.WriteLine("  --check    List migration status without changing the database.");
    Console.WriteLine("  --seed     Apply migrations, then run configured seed operations.");
    Console.WriteLine("Exit codes: 0 = success, 1 = pending migrations in check mode, 2 = error.");
    return 0;
}

if (args.Length > 1 || args.Length == 1 && args[0] is not ("--check" or "--seed"))
{
    Console.Error.WriteLine("Invalid arguments.");
    Console.Error.WriteLine(usage);
    return 2;
}

bool checkOnly = args.Length == 1 && args[0] == "--check";
bool seedRequested = args.Length == 1 && args[0] == "--seed";
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

using ILoggerFactory loggerFactory = LoggerFactory.Create(builder =>
    builder.AddSimpleConsole(options => options.SingleLine = true));
ILogger logger = loggerFactory.CreateLogger("Cinema.DbUpdater");

string environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
logger.LogInformation("Environment: {Environment}", environment);

IConfiguration configuration;
try
{
    var configurationBuilder = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true);

    if (environment == "Development")
    {
        configurationBuilder.AddUserSecrets<Program>(optional: true);
    }

    configuration = configurationBuilder.AddEnvironmentVariables().Build();
}
catch (Exception exception)
{
    logger.LogError("Configuration could not be loaded ({ExceptionType}). Check the updater's appsettings files.",
        exception.GetType().Name);
    return 2;
}

string? connectionString = configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString))
{
    logger.LogError("Missing ConnectionStrings:DefaultConnection. Set ConnectionStrings__DefaultConnection or an updater user secret.");
    return 2;
}

try
{
    var connection = new NpgsqlConnectionStringBuilder(connectionString);
    if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database))
    {
        logger.LogError("ConnectionStrings:DefaultConnection must specify Host and Database.");
        return 2;
    }
}
catch (Exception)
{
    logger.LogError("ConnectionStrings:DefaultConnection is invalid. Check its PostgreSQL connection string format.");
    return 2;
}

string operation = "Database configuration";
try
{
    await using var dataSource = DatabaseContextConfiguration.CreateDataSource(connectionString);
    var options = new DbContextOptionsBuilder<ApplicationDbContext>();
    DatabaseContextConfiguration.Configure(options, dataSource);
    await using var context = new ApplicationDbContext(options.Options);

    operation = "Database connection";
    logger.LogInformation("Connecting to PostgreSQL...");
    await context.Database.OpenConnectionAsync(cancellation.Token);

    operation = "Migration check";
    string[] applied = (await context.Database.GetAppliedMigrationsAsync(cancellation.Token)).ToArray();
    string[] pending = (await context.Database.GetPendingMigrationsAsync(cancellation.Token)).ToArray();
    logger.LogInformation("Applied migrations: {Count}", applied.Length);
    logger.LogInformation("Pending migrations: {Count}", pending.Length);

    foreach (string migration in pending)
    {
        logger.LogInformation("Pending: {Migration}", migration);
    }

    if (checkOnly)
    {
        logger.LogInformation(pending.Length == 0 ? "Database is up to date." : "Database has pending migrations.");
        return pending.Length == 0 ? 0 : 1;
    }

    if (pending.Length > 0)
    {
        operation = "Migration application";
        logger.LogInformation("Applying migrations...");
        await context.Database.MigrateAsync(cancellation.Token);
        logger.LogInformation("Migrations applied successfully.");
    }
    else
    {
        logger.LogInformation("Database is up to date.");
    }

    if (seedRequested)
    {
        operation = "Seeding";
        await SeedOperations.RunAsync(context, cancellation.Token);
        logger.LogInformation("No seed operations are configured; no seed data was changed.");
    }

    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    logger.LogError("{Operation} cancelled.", operation);
    return 2;
}
catch (Exception exception)
{
    logger.LogError("{Operation} failed ({ExceptionType}). Check PostgreSQL availability, credentials, and database state.",
        operation, exception.GetType().Name);
    return 2;
}
