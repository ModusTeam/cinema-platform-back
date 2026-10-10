using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Application.Orders.Services;
using Cinema.Application.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using MassTransit;
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
    public async Task Reservation_Persists_Multiple_Tickets_With_One_Tracked_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Seat second = await database.AddSeatAsync();

        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);

        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders.Include(o => o.Tickets)
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        order.Status.Should().Be(OrderStatus.Pending);
        order.TotalAmount.Should().Be(100m);
        order.PaidAmount.Should().Be(100m);
        order.Tickets.Should().HaveCount(2);
        order.Tickets.Select(t => t.SeatId).Should().BeEquivalentTo([database.Seat.Id, second.Id]);
        order.Tickets.Should().OnlyContain(t => t.OrderId == order.Id && t.Order == order &&
            t.SessionId == database.Session.Id && t.PriceSnapshot == 50m && t.TicketStatus == TicketStatus.Valid);
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Gold_Checkout_Persists_Ticket_And_Order_Discount_Together()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>())
            .Returns((true, ""));
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("gold-postgres"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m,
                database.Seat.Id.Value, 50m, 30m, null)));
        OrderCheckoutOrchestrator checkout = new(checkoutContext, loyalty, payment,
            Substitute.For<ISeatLockingService>(), gold, Substitute.For<IPublishEndpoint>(),
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, Guid.NewGuid(),
            [Guid.NewGuid()], reserved.Value, true, false, "token", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().ContainSingle();
        await gold.Received(1).CalculateAsync(Arg.Is<Session>(s => s.Id == database.Session.Id),
            Arg.Is<IReadOnlyCollection<GoldUpgradeTicketPrice>>(tickets =>
                tickets.Count == 1 && tickets.Single().SeatId == database.Seat.Id.Value),
            Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.Include(o => o.Tickets)
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(30m);
        persisted.PaidAmount.Should().Be(30m);
        persisted.Tickets.Single().PriceSnapshot.Should().Be(30m);
        persisted.Tickets.Single().IsGoldUpgraded.Should().BeTrue();
        persisted.Tickets.Single().Order.Should().Be(persisted);
    }

    [Fact]
    public async Task Checkout_Publishes_Persisted_Ticket_Count_For_Multiple_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(100m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("two-tickets"));
        IPublishEndpoint publisher = Substitute.For<IPublishEndpoint>();
        OrderCheckoutOrchestrator checkout = new(checkoutContext, Substitute.For<ILoyaltyService>(), payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(), publisher,
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, Guid.NewGuid(),
            [Guid.NewGuid()], reserved.Value, false, false, "token", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().HaveCount(2);
        await publisher.Received(1).Publish(Arg.Is<TicketPurchasedEvent>(e =>
            e.OrderId == reserved.Value && e.TotalAmount == 100m && e.TicketsCount == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gold_Checkout_With_Equal_Prices_Upgrades_One_Tracked_Ticket()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>())
            .Returns((true, ""));
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(80m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("equal-price"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m,
                database.Seat.Id.Value, 50m, 30m, null)));
        OrderCheckoutOrchestrator checkout = new(checkoutContext, loyalty, payment,
            Substitute.For<ISeatLockingService>(), gold, Substitute.For<IPublishEndpoint>(),
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value, second.Id.Value], reserved.Value, true, false, "token",
            TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.Include(o => o.Tickets)
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(80m);
        persisted.PaidAmount.Should().Be(80m);
        persisted.Tickets.Should().ContainSingle(t => t.IsGoldUpgraded && t.PriceSnapshot == 30m);
        persisted.Tickets.Should().ContainSingle(t => !t.IsGoldUpgraded && t.PriceSnapshot == 50m);
    }

    [Fact]
    public async Task Checkout_Confirmation_Count_Respects_Ticket_Query_Filter()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext checkoutContext = database.NewContext();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("filtered-ticket"));
        IPublishEndpoint publisher = Substitute.For<IPublishEndpoint>();
        OrderCheckoutOrchestrator checkout = new(checkoutContext, Substitute.For<ILoyaltyService>(), payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(), publisher,
            NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, false, false, "token", TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().BeEmpty();
        await publisher.Received(1).Publish(Arg.Is<TicketPurchasedEvent>(e =>
            e.OrderId == reserved.Value && e.TicketsCount == 0), Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

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
    public async Task Persistence_Failure_Rolls_Back_Order_And_Tickets_And_Allows_Retry()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        IPriceCalculator overflowingCalculator = Substitute.For<IPriceCalculator>();
        overflowingCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>())
            .Returns(999999999999999999m);

        Func<Task> reserve = async () => await database.ReserveAsync(database.Seat.Id.Value, calculator: overflowingCalculator);
        await reserve.Should().ThrowAsync<DbUpdateException>();
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

    [Fact]
    public async Task Saving_Order_Tracks_Tickets_And_Database_Cascades_Their_Deletion()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Order order = Order.Create(database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        database.Context.Orders.Add(order);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext verify = database.NewContext();
        Ticket ticket = await verify.Tickets.AsNoTracking()
            .Include(t => t.Order).Include(t => t.Session).Include(t => t.Seat)
            .SingleAsync(TestContext.Current.CancellationToken);
        ticket.OrderId.Should().Be(order.Id);
        ticket.Order!.Id.Should().Be(order.Id);
        ticket.Session!.Id.Should().Be(database.Session.Id);
        ticket.Seat!.Id.Should().Be(database.Seat.Id);

        await verify.Orders.Where(o => o.Id == order.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext afterDelete = database.NewContext();
        (await afterDelete.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Theory]
    [InlineData(TicketStatus.Valid)]
    [InlineData(TicketStatus.Used)]
    public async Task Partial_Index_Rejects_Valid_Or_Used_Duplicate_But_Allows_Refunded_Tickets(TicketStatus existingStatus)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Order first = Order.Create(database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        database.Context.Orders.Add(first);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Ticket original = first.Tickets.Single();
        if (existingStatus == TicketStatus.Used)
        {
            original.MarkAsUsed();
            await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (ApplicationDbContext duplicateContext = database.NewContext())
        {
            Order duplicate = Order.Create(database.UserId, database.Session, [database.Seat],
                new() { [database.Seat.Id] = 50m });
            duplicateContext.Orders.Add(duplicate);
            Func<Task> saveDuplicate = () => duplicateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            DbUpdateException failure = (await saveDuplicate.Should().ThrowAsync<DbUpdateException>()).Which;
            failure.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        // Refunded tickets are outside the partial index, including multiple tickets for one seat.
        Order refundedOne = Order.Create(database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        refundedOne.Tickets.Single().MarkAsRefunded();
        Order refundedTwo = Order.Create(database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        refundedTwo.Tickets.Single().MarkAsRefunded();
        database.Context.Orders.AddRange(refundedOne, refundedTwo);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(3);
        (await verify.Tickets.CountAsync(t => t.TicketStatus == TicketStatus.Refunded,
            TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Ticket_Query_Filter_Hides_Tickets_In_Inactive_Halls()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync();
        Order order = Order.Create(database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        database.Context.Orders.Add(order);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.Context.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
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

        public async Task<Seat> AddSeatAsync()
        {
            Seat second = Seat.New(EntityId<Seat>.New(), "A", 2, 1, 2, SeatStatus.Active,
                Seat.HallId, Seat.SeatTypeId);
            Context.Seats.Add(second);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return second;
        }

        public async Task<Result<Guid>> ReserveAsync(Guid seatId, Guid? sessionId = null, IPriceCalculator? calculator = null)
            => await ReserveAsync([seatId], sessionId, calculator);

        public async Task<Result<Guid>> ReserveAsync(List<Guid> seatIds, Guid? sessionId = null, IPriceCalculator? calculator = null)
        {
            IPriceCalculator priceCalculator = calculator ?? Substitute.For<IPriceCalculator>();
            if (calculator is null)
                priceCalculator.CalculatePrice(Arg.Any<Pricing>(), Arg.Any<EntityId<SeatType>>(), Arg.Any<DateTime>()).Returns(50m);
            await using ApplicationDbContext context = NewContext();
            return await new OrderReservationService(context, priceCalculator)
                .ReserveOrderAsync(_userId, sessionId ?? Session.Id.Value, seatIds, TestContext.Current.CancellationToken);
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
