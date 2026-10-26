using System.Diagnostics;
using Cinema.Infrastructure.Persistence;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Cinema.DbUpdaterTests;

public class DbUpdaterCliTests
{
    [Fact]
    public async Task Seat_Index_Migration_Preserves_Legacy_Pending_Order_And_Valid_Ticket()
    {
        string? testConnectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(testConnectionString))
            Assert.Skip("Set CINEMA_TEST_POSTGRES_CONNECTION to an isolated pgvector database named cinema_orders_test_*.");

        NpgsqlConnectionStringBuilder builder = new(testConnectionString);
        if (builder.Database is null || !builder.Database.StartsWith("cinema_orders_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("Migration tests require an isolated cinema_orders_test_* base database.");

        string databaseName = $"cinema_dbupdater_test_{Guid.NewGuid():N}";
        builder.Database = "postgres";
        string adminConnectionString = builder.ConnectionString;
        await using (NpgsqlConnection admin = new(adminConnectionString))
        {
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand create = new($"CREATE DATABASE {databaseName}", admin);
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        builder.Database = databaseName;
        string connectionString = builder.ConnectionString;
        try
        {
            await using NpgsqlDataSource dataSource = DatabaseContextConfiguration.CreateDataSource(connectionString);
            DbContextOptionsBuilder<ApplicationDbContext> options = new();
            DatabaseContextConfiguration.Configure(options, dataSource);
            await using ApplicationDbContext context = new(options.Options);
            // Pin the historical schema this regression was written against.
            string previousMigration = "20261017222608_AddMassTransitOutbox";
            await context.GetService<IMigrator>().MigrateAsync(previousMigration, TestContext.Current.CancellationToken);

            Guid userId = Guid.NewGuid();
            string suffix = Guid.NewGuid().ToString("N");
            Movie movie = Movie.CreateManual($"Migration test {suffix}", "test", 90,
                DateTime.UtcNow.Year, MovieStatus.ComingSoon);
            Hall hall = Hall.Create(EntityId<Hall>.New(), $"Migration test hall {suffix}");
            SeatType seatType = SeatType.New(EntityId<SeatType>.New(), $"Standard {suffix}", null);
            Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
            hall.ApplyLayout([seat]);
            Pricing pricing = Pricing.New(EntityId<Pricing>.New(), $"Migration test pricing {suffix}");
            Session session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
                DateTime.UtcNow.AddDays(2).AddHours(2), movie.Id, hall.Id, pricing.Id);
            Order order = Order.New(EntityId<Order>.New(), 50m, userId, session.Id);
            Ticket ticket = Ticket.New(EntityId<Ticket>.New(), 50m, TicketStatus.Valid,
                order.Id, session.Id, seat.Id);
            // Seed old Identity columns without using the newer User mapping.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "AspNetUsers" (id, user_name, email_confirmed, phone_number_confirmed,
                    two_factor_enabled, lockout_enabled, access_failed_count)
                VALUES ({userId}, {$"migration-{suffix}"}, false, false, false, false, 0)
                """, TestContext.Current.CancellationToken);
            context.Movies.Add(movie);
            context.Halls.Add(hall);
            context.SeatTypes.Add(seatType);
            context.Pricings.Add(pricing);
            context.Sessions.Add(session);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            // Seed the previous schema explicitly: the current EF model already includes PaidAt.
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO orders (id, total_amount, paid_amount, booking_date, status, user_id, session_id, points_used)
                VALUES ({order.Id.Value}, {order.TotalAmount}, {order.PaidAmount}, {order.BookingDate},
                    {(int)OrderStatus.Pending}, {userId}, {session.Id.Value}, {0});
                INSERT INTO tickets (id, price_snapshot, ticket_status, order_id, session_id, seat_id)
                VALUES ({ticket.Id.Value}, {ticket.PriceSnapshot}, {(int)TicketStatus.Valid},
                    {order.Id.Value}, {session.Id.Value}, {seat.Id.Value});
                """, TestContext.Current.CancellationToken);
            Guid legacyPaidOrder = Guid.NewGuid();
            await context.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO orders (id, total_amount, paid_amount, booking_date, status,
                    user_id, session_id, points_used, payment_transaction_id)
                VALUES ({legacyPaidOrder}, {50m}, {50m}, {DateTime.UtcNow}, {(int)OrderStatus.Paid},
                    {userId}, {session.Id.Value}, {0}, {"legacy-transaction"});
                """, TestContext.Current.CancellationToken);

            ProcessResult update = await RunAsync([], connectionString);
            Assert.Equal(0, update.ExitCode);
            Assert.Equal(2, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM orders WHERE paid_at IS NULL"));
            Assert.Equal((int)OrderStatus.Paid, await ScalarAsync<int>(connectionString,
                "SELECT status FROM orders WHERE id = @id", legacyPaidOrder));
            Assert.Equal(3, await ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name IN ('inbox_state', 'outbox_message', 'outbox_state')"));
            Assert.Equal((int)OrderStatus.Pending, await ScalarAsync<int>(connectionString,
                "SELECT status FROM orders WHERE id = @id", order.Id.Value));
            Assert.Equal((int)TicketStatus.Valid, await ScalarAsync<int>(connectionString,
                "SELECT ticket_status FROM tickets WHERE id = @id", ticket.Id.Value));
            string indexDefinition = await ScalarAsync<string>(connectionString,
                "SELECT indexdef FROM pg_indexes WHERE indexname = 'ix_tickets_session_id_seat_id'");
            Assert.Contains("ticket_status = ANY (ARRAY[0, 1, 3])", indexDefinition);

            Order competing = Order.New(EntityId<Order>.New(), 50m, userId, session.Id);
            context.Orders.Add(competing);
            context.Tickets.Add(Ticket.Reserve(EntityId<Ticket>.New(), 50m,
                competing.Id, session.Id, seat.Id));
            DbUpdateException conflict = await Assert.ThrowsAsync<DbUpdateException>(() =>
                context.SaveChangesAsync(TestContext.Current.CancellationToken));
            Assert.Equal(PostgresErrorCodes.UniqueViolation,
                Assert.IsType<PostgresException>(conflict.InnerException).SqlState);
        }
        finally
        {
            await using NpgsqlConnection admin = new(adminConnectionString);
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand drop = new($"DROP DATABASE {databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

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
