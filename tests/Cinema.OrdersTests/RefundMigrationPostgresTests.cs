using Cinema.Infrastructure.Persistence;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
namespace Cinema.OrdersTests;

public class RefundMigrationPostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Additive_migration_applies_clean_and_previous_schema_and_installs_immutable_guards(bool prior)
    {
        string? connection = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connection)) Assert.Skip("Set isolated disposable cinema_orders_test_* PostgreSQL connection");
        NpgsqlConnectionStringBuilder builder = new(connection);
        if (builder.Database?.StartsWith("cinema_orders_test_", StringComparison.Ordinal) != true || builder.Host is not ("127.0.0.1" or "localhost")) throw new InvalidOperationException("Disposable test database required");
        string name = "cinema_orders_test_refund_migration_" + Guid.NewGuid().ToString("N"); builder.Database = "postgres";
        await using NpgsqlConnection admin = new(builder.ConnectionString); await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using (NpgsqlCommand create = new($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        builder.Database = name;
        try
        {
            await using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(builder.ConnectionString, p => p.UseVector()).UseSnakeCaseNamingConvention().Options);
            Order? historical = null;
            if (prior)
            {
                await db.GetService<IMigrator>().MigrateAsync("20261102185239_AddAuthoritativePaidCurrency", TestContext.Current.CancellationToken);
                historical = await SeedHistoricalPaidSource(db);
            }
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
            db.Database.HasPendingModelChanges().Should().BeFalse();
            (await db.Database.GetPendingMigrationsAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
            (await db.PaidCancellations.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
            if (historical is not null)
            {
                Order retained = await db.Orders.AsNoTracking().SingleAsync(x => x.Id == historical.Id, TestContext.Current.CancellationToken);
                retained.PaidCurrency.Should().BeNull(); retained.PaymentTransactionId.Should().Be("historical-unverified-charge");
                retained.PaidAmount.Should().Be(100); retained.Status.Should().Be(OrderStatus.Paid);
                retained.PaidAt.Should().Be(historical.PaidAt);
            }
            (await db.Database.SqlQuery<string>($"""SELECT data_type AS "Value" FROM information_schema.columns WHERE table_schema='public' AND table_name='refund_provider_observations' AND column_name='provider_reference' """).SingleAsync(TestContext.Current.CancellationToken)).Should().Be("text");
            (await db.Database.SqlQuery<int>($"""SELECT count(*)::integer AS "Value" FROM pg_trigger WHERE tgname IN ('protect_refund_operation','protect_refund_observation') AND NOT tgisinternal """).SingleAsync(TestContext.Current.CancellationToken)).Should().Be(2);
            (await db.Database.SqlQuery<int>($"""SELECT count(*)::integer AS "Value" FROM pg_trigger WHERE tgname IN ('protect_paid_cancellation','arbitrate_paid_cancellation','arbitrate_refund_operation') AND NOT tgisinternal """).SingleAsync(TestContext.Current.CancellationToken)).Should().Be(3);
            // Isolate the trigger function from parent graphs; keep one connection for the temporary probe.
            await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            await db.Database.ExecuteSqlRawAsync("CREATE TEMP TABLE refund_guard_probe (id uuid, amount numeric, state text); CREATE TRIGGER protect_refund_probe BEFORE UPDATE OR DELETE ON refund_guard_probe FOR EACH ROW EXECUTE FUNCTION protect_refund_operation(); INSERT INTO refund_guard_probe(id,amount,state) VALUES('00000000-0000-4000-8000-000000000001',100,'Approved');", TestContext.Current.CancellationToken);
            Func<Task> mutate = () => db.Database.ExecuteSqlRawAsync("UPDATE refund_guard_probe SET amount=200", TestContext.Current.CancellationToken);
            (await mutate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
        }
        finally { await using NpgsqlCommand drop = new($"DROP DATABASE {name} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(CancellationToken.None); }
    }

    private static async Task<Order> SeedHistoricalPaidSource(ApplicationDbContext db)
    {
        User user = new() { Id = Guid.NewGuid(), UserName = "migration-historical" };
        Hall hall = Hall.Create(EntityId<Hall>.New(), "migration-historical");
        Movie movie = Movie.CreateManual("migration-historical", "test", 90, 2026, MovieStatus.ComingSoon);
        Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "migration-historical");
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2), movie.Id, hall.Id, pricing.Id);
        Order order = Order.New(EntityId<Order>.New(), 100, user.Id, session.Id);
        order.MarkAsPaid("historical-unverified-charge");
        db.AddRange(user, hall, movie, pricing, session, order);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await db.Entry(order).ReloadAsync(TestContext.Current.CancellationToken);
        return order;
    }
}
