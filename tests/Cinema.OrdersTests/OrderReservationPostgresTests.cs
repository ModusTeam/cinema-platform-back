using Cinema.Application.Common.Interfaces;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

// Only run with a disposable pgvector PostgreSQL database named cinema_orders_test_*.
public class OrderReservationPostgresTests
{
    [Fact]
    public async Task Reservation_Creates_Pending_Order_With_Valid_Ticket_And_Rejects_Resale()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);

        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders.Include(o => o.Tickets).SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        order.Status.Should().Be(OrderStatus.Pending);
        order.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Valid);
        order.TotalAmount.Should().Be(50m);
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Order.SeatsSold");
        (await verify.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Missing_And_Inactive_Seats_And_Missing_Session_Do_Not_Create_Orders()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        (await database.ReserveAsync(Guid.NewGuid())).Error.Code.Should().Be("Order.SeatsNotFound");
        database.Seat.SetStatus(SeatStatus.Maintenance);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Order.SeatsNotActive");
        database.Seat.SetStatus(SeatStatus.Active);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value, Guid.NewGuid())).Error.Code.Should().Be("Session.NotFound");
        (await database.Context.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Pricing_Failure_Rolls_Back_Reservation_And_Allows_Retry()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        IPriceCalculator failingCalculator = Substitute.For<IPriceCalculator>();
        failingCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>())
            .Returns(_ => throw new InvalidOperationException("pricing unavailable"));

        Func<Task> reserve = async () => await database.ReserveAsync(database.Seat.Id.Value, calculator: failingCalculator);
        await reserve.Should().ThrowAsync<InvalidOperationException>();
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Independent_Row_Lock_Causes_Nowait_Conflict()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (NpgsqlCommand command = new("SELECT id FROM seats WHERE id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", database.Seat.Id.Value);
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken); // Lock acquired before reservation starts.
        }

        Func<Task> reserve = async () => await database.ReserveAsync(database.Seat.Id.Value);
        InvalidOperationException failure = (await reserve.Should().ThrowAsync<InvalidOperationException>()).Which;
        failure.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Checkout_Failure_After_Reservation_Leaves_Committed_Pending_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(database.Session.Id.Value, database.Seat.Id.Value, database.UserId,
            Arg.Any<CancellationToken>()).Returns(true);
        IPriceCalculator calculator = Substitute.For<IPriceCalculator>();
        calculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(50m);
        IOrderCheckoutOrchestrator checkout = Substitute.For<IOrderCheckoutOrchestrator>();
        Guid reservedOrderId = Guid.Empty;
        checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value, Arg.Any<List<Guid>>(),
                Arg.Any<Guid>(), false, false, "token", Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                reservedOrderId = call.ArgAt<Guid>(3);
                return Result.Failure<Guid>(new Error("Payment.Failed", "declined"));
            });
        CreateOrderCommandHandler handler = new(new OrderReservationService(database.Context, calculator), currentUser,
            locks, checkout, NullLogger<CreateOrderCommandHandler>.Instance);

        Result<Guid> result = await handler.Handle(new CreateOrderCommand
        {
            SessionId = database.Session.Id.Value,
            SeatIds = [database.Seat.Id.Value],
            PaymentToken = "token"
        }, TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Payment.Failed");
        reservedOrderId.Should().NotBeEmpty();
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.Include(o => o.Tickets).SingleAsync(o => o.Id == new EntityId<Order>(reservedOrderId),
            TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Pending);
        persisted.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Valid);
    }

    private sealed class ReservationDatabase : IAsyncDisposable
    {
        public string ConnectionString { get; }
        public ApplicationDbContext Context { get; }
        public Seat Seat { get; private set; } = null!;
        public Session Session { get; private set; } = null!;
        private readonly Guid _userId = Guid.NewGuid();
        public Guid UserId => _userId;
        private readonly string _adminConnectionString;
        private readonly string _databaseName;

        private ReservationDatabase(string connectionString, string adminConnectionString, string databaseName)
        {
            ConnectionString = connectionString;
            _adminConnectionString = adminConnectionString;
            _databaseName = databaseName;
            Context = NewContext();
        }

        public static async Task<ReservationDatabase> CreateAsync()
        {
            string? connectionString = Environment.GetEnvironmentVariable("CINEMA_TEST_POSTGRES_CONNECTION");
            if (string.IsNullOrWhiteSpace(connectionString))
                Assert.Skip("Set CINEMA_TEST_POSTGRES_CONNECTION to an isolated disposable pgvector database named cinema_orders_test_*.");
            NpgsqlConnectionStringBuilder builder = new(connectionString);
            if (builder.Database is null || !builder.Database.StartsWith("cinema_orders_test_", StringComparison.Ordinal))
                throw new InvalidOperationException("Reservation tests require a disposable cinema_orders_test_* database.");

            string databaseName = $"cinema_orders_test_{Guid.NewGuid():N}";
            builder.Database = "postgres";
            string adminConnectionString = builder.ConnectionString;
            await using (NpgsqlConnection admin = new(adminConnectionString))
            {
                await admin.OpenAsync(TestContext.Current.CancellationToken);
                await using NpgsqlCommand create = new($"CREATE DATABASE {databaseName}", admin);
                await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            builder.Database = databaseName;
            ReservationDatabase database = new(builder.ConnectionString, adminConnectionString, databaseName);
            try
            {
                await database.Context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                User user = new() { Id = database._userId, UserName = $"order-test-{Guid.NewGuid():N}" };
                string suffix = Guid.NewGuid().ToString("N");
                Movie movie = Movie.CreateManual($"Reservation test {suffix}", "test", 90, DateTime.UtcNow.Year, MovieStatus.ComingSoon);
                Hall hall = Hall.Create(EntityId<Hall>.New(), $"Reservation test hall {suffix}");
                SeatType seatType = SeatType.New(EntityId<SeatType>.New(), $"Standard {suffix}", null);
                database.Seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
                hall.ApplyLayout([database.Seat]);
                Pricing pricing = Pricing.New(EntityId<Pricing>.New(), $"Reservation test pricing {suffix}");
                PricingItem item = PricingItem.New(EntityId<PricingItem>.New(), 50m, pricing.Id, seatType.Id, null, null, null);
                database.Session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
                    DateTime.UtcNow.AddDays(2).AddHours(2), movie.Id, hall.Id, pricing.Id);
                database.Context.Users.Add(user);
                database.Context.Movies.Add(movie);
                database.Context.Halls.Add(hall);
                database.Context.SeatTypes.Add(seatType);
                database.Context.Pricings.Add(pricing);
                database.Context.PricingItems.Add(item);
                database.Context.Sessions.Add(database.Session);
                await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
                return database;
            }
            catch
            {
                await database.DisposeAsync();
                throw;
            }
        }

        public ApplicationDbContext NewContext()
        {
            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(ConnectionString, x => x.UseVector()).UseSnakeCaseNamingConvention().Options;
            return new ApplicationDbContext(options);
        }

        public async Task<Result<Guid>> ReserveAsync(Guid seatId, Guid? sessionId = null, IPriceCalculator? calculator = null)
        {
            IPriceCalculator priceCalculator = calculator ?? Substitute.For<IPriceCalculator>();
            if (calculator is null)
                priceCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(50m);
            await using ApplicationDbContext context = NewContext();
            return await new OrderReservationService(context, priceCalculator)
                .ReserveOrderAsync(_userId, sessionId ?? Session.Id.Value, [seatId], TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await using NpgsqlConnection admin = new(_adminConnectionString);
            await admin.OpenAsync();
            await using NpgsqlCommand drop = new($"DROP DATABASE {_databaseName} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
