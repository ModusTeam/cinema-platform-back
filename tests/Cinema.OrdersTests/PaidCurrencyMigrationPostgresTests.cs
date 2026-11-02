using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Cinema.OrdersTests;

public class PaidCurrencyMigrationPostgresTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clean_and_prior_schema_keep_legacy_currency_unknown_and_protect_confirmed_currency(bool prior)
    {
        string? source = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(source)) Assert.Skip("Set isolated disposable cinema_orders_test_* connection.");
        NpgsqlConnectionStringBuilder builder = new(source);
        if (builder.Database?.StartsWith("cinema_orders_test_", StringComparison.Ordinal) != true || builder.Host is not ("localhost" or "127.0.0.1"))
            throw new InvalidOperationException("Disposable loopback PostgreSQL required.");
        string name = "cinema_orders_test_currency_" + Guid.NewGuid().ToString("N"); builder.Database = "postgres";
        await using NpgsqlConnection admin = new(builder.ConnectionString); await admin.OpenAsync(Ct);
        await using (NpgsqlCommand create = new($"CREATE DATABASE {name}", admin)) await create.ExecuteNonQueryAsync(Ct);
        builder.Database = name;
        try
        {
            await using ApplicationDbContext db = new(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(builder.ConnectionString, p => p.UseVector()).UseSnakeCaseNamingConvention().Options);
            Guid user = Guid.NewGuid(); EntityId<Session> session = EntityId<Session>.New(); Guid legacy = Guid.NewGuid();
            DateTime paid = new(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
            if (prior)
            {
                await db.GetService<IMigrator>().MigrateAsync("20261102180649_PreserveFullRefundProviderReferences", Ct);
                await AddParents(db, user, session);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO orders (id,total_amount,paid_amount,booking_date,paid_at,status,payment_transaction_id,user_id,session_id,points_used,
                    gold_checkout_state,gold_original_ticket_price,gold_original_total,gold_original_paid,gold_use_confirmed,gold_payment_outcome_unknown)
                    VALUES ({legacy},100,100,{paid},{paid},{(int)OrderStatus.Paid},{"legacy-charge"},{user},{session.Value},0,0,0,0,0,false,false)
                    """, Ct);
            }
            await db.Database.MigrateAsync(Ct);
            db.Database.HasPendingModelChanges().Should().BeFalse();
            if (prior)
            {
                Order old = await db.Orders.SingleAsync(x => x.Id == new EntityId<Order>(legacy), Ct);
                old.PaidCurrency.Should().BeNull(); old.PaidAt.Should().Be(paid); old.PaidAmount.Should().Be(100);
                old.PaymentTransactionId.Should().Be("legacy-charge"); old.Status.Should().Be(OrderStatus.Paid);
                Func<Task> fabricated = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET paid_currency='UAH' WHERE id={legacy}", Ct);
                (await fabricated.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
            }
            else await AddParents(db, user, session);
            Order known = Order.New(EntityId<Order>.New(), 100, user, session); known.MarkAsPaid("known-charge", "USD");
            db.Add(known); await db.SaveChangesAsync(Ct);
            db.Entry(known).Property(x => x.PaidCurrency).CurrentValue = "EUR";
            Func<Task> trackedRewrite = () => db.SaveChangesAsync(Ct);
            await trackedRewrite.Should().ThrowAsync<InvalidOperationException>(); db.ChangeTracker.Clear();
            Func<Task> sqlRewrite = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET paid_currency='EUR' WHERE id={known.Id.Value}", Ct);
            (await sqlRewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0001");
            (await db.Orders.SingleAsync(x => x.Id == known.Id, Ct)).PaidCurrency.Should().Be("USD");
        }
        finally { await using NpgsqlCommand drop = new($"DROP DATABASE {name} WITH (FORCE)", admin); await drop.ExecuteNonQueryAsync(CancellationToken.None); }
    }

    private static async Task AddParents(ApplicationDbContext db, Guid userId, EntityId<Session> sessionId)
    {
        User user = new() { Id = userId, UserName = "currency-migration" };
        Hall hall = Hall.Create(EntityId<Hall>.New(), "currency");
        Movie movie = Movie.CreateManual("currency", "test", 90, 2026, MovieStatus.ComingSoon);
        Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "currency"); DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(sessionId, start, start.AddHours(2), movie.Id, hall.Id, pricing.Id);
        db.AddRange(user, hall, movie, pricing, session); await db.SaveChangesAsync(Ct);
    }
}
