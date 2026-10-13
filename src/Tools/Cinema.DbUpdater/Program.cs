using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

if (args.Length > 1 || args.Length == 1 && args[0] != "--check")
{
    Console.Error.WriteLine("Usage: Cinema.DbUpdater [--check]");
    return 2;
}

bool checkOnly = args.Length == 1;
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

try
{
    string environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production";
    IConfiguration configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile($"appsettings.{environment}.json", optional: true)
        .AddUserSecrets<Program>(optional: true)
        .AddEnvironmentVariables()
        .Build();

    string? connectionString = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(connectionString))
    {
        Console.Error.WriteLine("Missing ConnectionStrings:DefaultConnection. Set ConnectionStrings__DefaultConnection or a user secret.");
        return 2;
    }

    await using var dataSource = DatabaseContextConfiguration.CreateDataSource(connectionString);
    var options = new DbContextOptionsBuilder<ApplicationDbContext>();
    DatabaseContextConfiguration.Configure(options, dataSource);
    await using var context = new ApplicationDbContext(options.Options);

    Console.WriteLine("Connecting to PostgreSQL...");
    await context.Database.OpenConnectionAsync(cancellation.Token);

    string[] applied = (await context.Database.GetAppliedMigrationsAsync(cancellation.Token)).ToArray();
    string[] pending = (await context.Database.GetPendingMigrationsAsync(cancellation.Token)).ToArray();
    Console.WriteLine($"Applied migrations: {applied.Length}");
    Console.WriteLine($"Pending migrations: {pending.Length}");

    foreach (string migration in pending)
    {
        Console.WriteLine($"  {migration}");
    }

    if (checkOnly)
    {
        Console.WriteLine(pending.Length == 0 ? "Database is up to date." : "Database has pending migrations.");
        return pending.Length == 0 ? 0 : 1;
    }

    if (pending.Length == 0)
    {
        Console.WriteLine("Database is up to date.");
        return 0;
    }

    Console.WriteLine("Applying migrations...");
    await context.Database.MigrateAsync(cancellation.Token);
    Console.WriteLine("Migrations applied successfully.");
    return 0;
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Database update cancelled.");
    return 2;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Database update failed: {exception}");
    return 2;
}
