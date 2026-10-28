using Cinema.Application.Common.Contracts;
using Cinema.Domain.Entities;
using Cinema.Infrastructure.Persistence;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Cinema.DbUpdaterTests;

public partial class DbUpdaterCliTests
{
    [Fact]
    public async Task Restage_Mode_Is_Parsed_Before_Configuration_And_Rejects_Invalid_Arguments()
    {
        ProcessResult valid = await RunAsync(["--restage-customer-fact", Guid.NewGuid().ToString()], "");
        Assert.Equal(2, valid.ExitCode);
        Assert.Contains("Missing ConnectionStrings", valid.Output);
        Assert.DoesNotContain("Invalid arguments", valid.Output);
        foreach (string[] args in new[] { new[] { "--restage-customer-fact" }, new[] { "--restage-customer-fact", "invalid" }, new[] { "--restage-customer-fact", Guid.NewGuid().ToString(), "--seed" } })
        {
            ProcessResult invalid = await RunAsync(args, "");
            Assert.Equal(2, invalid.ExitCode);
            Assert.Contains("Invalid arguments", invalid.Output);
        }
    }

    [Fact]
    public async Task Restage_Uses_Original_History_After_Outbox_Cleanup_Without_Source_Or_Legacy_Mutation()
    {
        await using ReplayDatabase fixture = await ReplayDatabase.CreateAsync(true);
        Guid userId = Guid.NewGuid(); Guid eventId = Guid.NewGuid(); string snapshot;
        await using (ApplicationDbContext db = fixture.Context())
        {
            User user = new() { Id = userId, UserName = "retained-source" };
            user.RecordRegistration(new DateTime(2024, 2, 29, 22, 0, 0, DateTimeKind.Utc));
            snapshot = CustomerFactsEnvelope.FromUser(user, eventId, "Registered").ToJson();
            db.Users.Add(user);
            db.CustomerFactRevisions.Add(CustomerFactRevision.Create(eventId, userId, 1, snapshot));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            ProcessResult replay = await RunAsync(["--restage-customer-fact", eventId.ToString()], fixture.Connection);
            Assert.Equal(0, replay.ExitCode);
            Assert.DoesNotContain("Applying migrations", replay.Output);
            Assert.DoesNotContain("Seeding", replay.Output);
            await using ApplicationDbContext db = fixture.Context();
            List<OutboxMessage> messages = await db.Set<OutboxMessage>().ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(attempt, messages.Count);
            foreach (OutboxMessage message in messages)
            {
                Assert.Equal(eventId, message.MessageId);
                Assert.Equal(snapshot, CustomerFactsEnvelope.FromJson(message.Body).ToJson());
                Assert.DoesNotContain("USER_DATE_OF_BIRTH_SET", message.Body);
            }
            Assert.Equal(snapshot, (await db.CustomerFactRevisions.SingleAsync(TestContext.Current.CancellationToken)).Snapshot);
            User user = await db.Users.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, user.CustomerFactsRevision); Assert.Null(user.DobFirstSetAtUtc);
            Assert.Equal(new DateTime(2024, 2, 29, 22, 0, 0, DateTimeKind.Utc), user.RegisteredAtUtc);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restage_Missing_Evidence_Or_Source_Schema_Fails_Without_Migration_Or_Seed(bool schemaReady)
    {
        await using ReplayDatabase fixture = await ReplayDatabase.CreateAsync(schemaReady);
        int before;
        await using (ApplicationDbContext db = fixture.Context())
            before = (await db.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count();
        ProcessResult result = await RunAsync(["--restage-customer-fact", Guid.NewGuid().ToString()], fixture.Connection);
        Assert.Equal(2, result.ExitCode);
        Assert.DoesNotContain("Applying migrations", result.Output);
        await using ApplicationDbContext verify = fixture.Context();
        Assert.Equal(before, (await verify.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken)).Count());
        Assert.Equal(0, await verify.Set<OutboxMessage>().CountAsync(TestContext.Current.CancellationToken));
        if (schemaReady) Assert.Contains("CUSTOMER_FACT_NOT_FOUND", result.Output);
    }

    private sealed record ReplayDatabase(string Connection, string Admin, string Name) : IAsyncDisposable
    {
        public ApplicationDbContext Context() => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(Connection, p => p.UseVector()).UseSnakeCaseNamingConvention().Options);

        public static async Task<ReplayDatabase> CreateAsync(bool completeSchema)
        {
            string? connection = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
            if (string.IsNullOrWhiteSpace(connection)) Assert.Skip("Set disposable CINEMA_TEST_POSTGRES_CONNECTION.");
            NpgsqlConnectionStringBuilder builder = new(connection);
            if (builder.Database?.StartsWith("cinema_orders_test_", StringComparison.Ordinal) != true)
                throw new InvalidOperationException("Not a disposable test target");
            string name = $"cinema_orders_test_restage_{Guid.NewGuid():N}";
            builder.Database = "postgres"; string admin = builder.ConnectionString;
            await using (NpgsqlConnection db = new(admin))
            {
                await db.OpenAsync(TestContext.Current.CancellationToken);
                await using NpgsqlCommand create = new($"CREATE DATABASE {name}", db);
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            builder.Database = name;
            ReplayDatabase fixture = new(builder.ConnectionString, admin, name);
            try
            {
                await using ApplicationDbContext context = fixture.Context();
                await context.GetService<IMigrator>().MigrateAsync(completeSchema ? null : "20261023192457_PersistOrderPaidAt", TestContext.Current.CancellationToken);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            await using NpgsqlConnection db = new(Admin); await db.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE {Name} WITH (FORCE)", db); await drop.ExecuteNonQueryAsync();
        }
    }
}
