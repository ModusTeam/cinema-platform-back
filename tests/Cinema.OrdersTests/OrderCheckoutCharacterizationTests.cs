using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Application.Orders.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

// These tests describe the current checkout behavior, including lifecycle defects.
public class OrderCheckoutCharacterizationTests
{
    [Fact]
    public async Task Missing_Or_Other_Users_Order_Is_Rejected_Before_Payment()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        foreach (Guid orderId in new[] { Guid.NewGuid(), fixture.Order.Id.Value })
        {
            Result<Guid> result = await fixture.CheckoutAsync(orderId: orderId, userId: Guid.NewGuid());
            result.Error.Code.Should().Be("Order.NotFound");
        }
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Already_Paid_Order_Returns_Id_Without_Another_Charge_Or_Publish()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Order.MarkAsPaid("original-transaction");
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Result<Guid> result = await fixture.CheckoutAsync();

        result.Value.Should().Be(fixture.Order.Id.Value);
        fixture.Order.PaymentTransactionId.Should().Be("original-transaction");
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await fixture.Publisher.DidNotReceiveWithAnyArgs().Publish(Arg.Any<TicketPurchasedEvent>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_Order_Cannot_Be_Retried()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Order.MarkAsFailed();
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Result<Guid> result = await fixture.CheckoutAsync();

        result.Error.Code.Should().Be("Order.InvalidState");
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Successful_Payment_Uses_Order_Amount_And_Publishes_Existing_Event()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("transaction-1"));

        Result<Guid> result = await fixture.CheckoutAsync(sessionId: Guid.NewGuid(), seatIds: [Guid.NewGuid()]);

        result.Value.Should().Be(fixture.Order.Id.Value);
        fixture.Order.Status.Should().Be(OrderStatus.Paid);
        fixture.Order.PaymentTransactionId.Should().Be("transaction-1");
        await fixture.Payment.Received(1).ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>());
        await fixture.Publisher.Received(1).Publish(Arg.Is<TicketPurchasedEvent>(e =>
            e.OrderId == fixture.Order.Id.Value && e.UserId == fixture.UserId &&
            e.TotalAmount == 50m && e.TicketsCount == 1), Arg.Any<CancellationToken>());
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().CalculateDiscountAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Publish_Exception_After_Charge_Attempts_Refund_And_Leaves_Persisted_Order_Pending()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("charged-transaction"));
        fixture.Publisher.Publish(Arg.Any<TicketPurchasedEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker unavailable"));

        Result<Guid> result = await fixture.CheckoutAsync();

        result.Error.Code.Should().Be("Order.ConfirmationFailed");
        await fixture.Payment.Received(1).RefundPaymentAsync("charged-transaction", Arg.Any<CancellationToken>());
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(o => o.Id == fixture.Order.Id,
            TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Pending);
        persisted.PaymentTransactionId.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decline_Or_Provider_Exception_Fails_Order_But_Leaves_Valid_Ticket(bool providerThrows)
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        if (providerThrows)
            fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
                .Returns<Task<PaymentResult>>(_ => throw new InvalidOperationException("provider unavailable"));
        else
            fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
                .Returns(PaymentResult.Failure("declined"));

        Result<Guid> result = await fixture.CheckoutAsync();

        result.Error.Code.Should().Be("Payment.Failed");
        result.Error.Description.Should().Be(providerThrows ? "Payment system error" : "declined");
        fixture.Order.Status.Should().Be(OrderStatus.Failed);
        fixture.Order.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Valid); // Existing sold-seat state.
        await fixture.Locks.DidNotReceiveWithAnyArgs().UnlockSeatsAsync(default, default!, default, TestContext.Current.CancellationToken);
        await fixture.Publisher.DidNotReceiveWithAnyArgs().Publish(Arg.Any<TicketPurchasedEvent>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pending_Order_Already_Has_A_Valid_Ticket()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Order.Status.Should().Be(OrderStatus.Pending);
        fixture.Order.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Loyalty_Disabled_For_Session_Does_Not_Call_Provider_Or_Payment()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync(loyaltyAllowed: false);
        Result<Guid> result = await fixture.CheckoutAsync(usePoints: true);
        result.Error.Code.Should().Be("Order.LoyaltyNotAllowed");
        fixture.Order.Status.Should().Be(OrderStatus.Pending);
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().CalculateDiscountAsync(default, default, TestContext.Current.CancellationToken);
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Loyalty_Discount_Unavailable_Leaves_Order_Pending()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 50m, Arg.Any<CancellationToken>())
            .Returns<Task<(bool, int, decimal)>>(_ => throw new InvalidOperationException("offline"));

        Result<Guid> result = await fixture.CheckoutAsync(usePoints: true);

        result.Error.Code.Should().Be("Loyalty.Unavailable");
        fixture.Order.Status.Should().Be(OrderStatus.Pending);
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Loyalty_Discount_Deducts_Points_And_Charges_Discounted_Amount()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 50m, Arg.Any<CancellationToken>())
            .Returns((true, 10, 40m));
        fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, 90, ""));
        fixture.Payment.ProcessPaymentAsync(40m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("discounted-transaction"));

        Result<Guid> result = await fixture.CheckoutAsync(usePoints: true);

        result.IsSuccess.Should().BeTrue();
        fixture.Order.PointsUsed.Should().Be(10);
        fixture.Order.PaidAmount.Should().Be(40m);
        await fixture.Loyalty.Received(1).DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value,
            DeterministicGuid.Create($"deduct-{fixture.Order.Id.Value}" ).ToString(), Arg.Any<CancellationToken>());
        await fixture.Payment.Received(1).ProcessPaymentAsync(40m, "UAH", "token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_Point_Deduction_Fails_Order_Without_Calling_Payment_Or_Refund()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 50m, Arg.Any<CancellationToken>()).Returns((true, 10, 40m));
        fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, 0, "insufficient"));

        Result<Guid> result = await fixture.CheckoutAsync(usePoints: true);

        result.Error.Code.Should().Be("Payment.Failed");
        fixture.Order.Status.Should().Be(OrderStatus.Failed);
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().RefundPointsAsync(default, default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Payment_Decline_After_Point_Deduction_Attempts_Deterministic_Refund()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 50m, Arg.Any<CancellationToken>()).Returns((true, 10, 40m));
        fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, 90, ""));
        fixture.Payment.ProcessPaymentAsync(40m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure("declined"));

        Result<Guid> result = await fixture.CheckoutAsync(usePoints: true);

        result.Error.Code.Should().Be("Payment.Failed");
        fixture.Order.Status.Should().Be(OrderStatus.Failed);
        await fixture.Loyalty.Received(1).RefundPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value,
            DeterministicGuid.Create($"refund-{fixture.Order.Id.Value}").ToString(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gold_Upgrade_Reduces_Price_Before_Payment()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("gold-transaction"));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true, sessionId: Guid.NewGuid(), seatIds: [Guid.NewGuid()]);

        result.IsSuccess.Should().BeTrue();
        fixture.Order.TotalAmount.Should().Be(30m);
        fixture.Order.Tickets.Single().IsGoldUpgraded.Should().BeTrue();
        await fixture.Payment.Received(1).ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>());
        await fixture.Gold.Received(1).CalculateAsync(Arg.Is<Session>(s => s.Id == fixture.Session.Id),
            Arg.Is<IReadOnlyCollection<GoldUpgradeTicketPrice>>(t => t.Count == 1 && t.Single().SeatId == fixture.Seat.Id.Value),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(50, true)]
    [InlineData(30, false)]
    public async Task Gold_Pricing_Uses_Standard_Price_To_Determine_Eligibility(int ticketPrice, bool expectedEligible)
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        SeatType standard = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        fixture.Context.SeatTypes.Add(standard);
        fixture.Context.PricingItems.Add(PricingItem.New(EntityId<PricingItem>.New(), 30m,
            fixture.Session.PricingId, standard.Id, null, null, null));
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Session session = await fixture.Context.Sessions.Include(s => s.Pricing)!
            .ThenInclude(p => p!.PricingItems)
            .SingleAsync(s => s.Id == fixture.Session.Id, TestContext.Current.CancellationToken);
        IPriceCalculator calculator = Substitute.For<IPriceCalculator>();
        calculator.CalculatePrice(Arg.Any<Pricing>(), standard.Id, Arg.Any<DateTime>()).Returns(30m);

        Result<GoldUpgradePricingQuote> result = await new GoldUpgradePricingService(fixture.Context, calculator)
            .CalculateAsync(session, [new GoldUpgradeTicketPrice(fixture.Seat.Id.Value, ticketPrice)],
                TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsApplied.Should().Be(expectedEligible);
        result.Value.BasePrice.Should().Be(30m);
        result.Value.DiscountAmount.Should().Be(expectedEligible ? 20m : 0m);
    }

    [Fact]
    public async Task Gold_No_Eligible_Ticket_Unlocks_Persisted_Seat_And_Does_Not_Consume()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(false, 50m, 0m, null, null, null, "not eligible")));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true, sessionId: Guid.NewGuid(), seatIds: [Guid.NewGuid()]);

        result.Error.Code.Should().Be("Order.NoEligibleTickets");
        await fixture.Locks.Received(1).UnlockSeatsAsync(fixture.Session.Id.Value,
            Arg.Is<IEnumerable<Guid>>(ids => ids.SequenceEqual(new[] { fixture.Seat.Id.Value })), fixture.UserId, Arg.Any<CancellationToken>());
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().UseGoldUpgradeAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gold_Consumption_Failure_Leaves_Order_Pending()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>())
            .Returns((false, "unavailable"));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true);

        result.Error.Code.Should().Be("Order.GoldUpgradeFailed");
        fixture.Order.Status.Should().Be(OrderStatus.Pending);
        fixture.Order.TotalAmount.Should().Be(50m);
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_Payment_After_Gold_Consumption_Attempts_Rollback_But_Keeps_Discounted_Ticket()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure("declined"));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true);

        result.Error.Code.Should().Be("Payment.Failed");
        fixture.Order.Status.Should().Be(OrderStatus.Failed);
        fixture.Order.TotalAmount.Should().Be(30m);
        fixture.Order.Tickets.Single().IsGoldUpgraded.Should().BeTrue();
        await fixture.Loyalty.Received(1).RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
    }

    private sealed class CheckoutFixture : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public Session Session { get; private set; } = null!;
        public Seat Seat { get; private set; } = null!;
        public Order Order { get; private set; } = null!;
        public IPaymentService Payment { get; } = Substitute.For<IPaymentService>();
        public ILoyaltyService Loyalty { get; } = Substitute.For<ILoyaltyService>();
        public ISeatLockingService Locks { get; } = Substitute.For<ISeatLockingService>();
        public IGoldUpgradePricingService Gold { get; } = Substitute.For<IGoldUpgradePricingService>();
        public IPublishEndpoint Publisher { get; } = Substitute.For<IPublishEndpoint>();

        private CheckoutFixture()
        {
            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Context = new CheckoutDbContext(options);
        }

        public static async Task<CheckoutFixture> CreateAsync(bool loyaltyAllowed = true)
        {
            CheckoutFixture fixture = new();
            Hall hall = Hall.Create(EntityId<Hall>.New(), "Checkout test hall");
            SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Premium", null);
            fixture.Seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
            hall.ApplyLayout([fixture.Seat]);
            Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "Checkout test pricing");
            fixture.Session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
                DateTime.UtcNow.AddDays(2).AddHours(2), EntityId<Movie>.New(), hall.Id, pricing.Id, loyaltyAllowed);
            fixture.Order = Order.Create(fixture.UserId, fixture.Session, [fixture.Seat],
                new Dictionary<EntityId<Seat>, decimal> { [fixture.Seat.Id] = 50m });
            fixture.Context.Halls.Add(hall);
            fixture.Context.SeatTypes.Add(seatType);
            fixture.Context.Pricings.Add(pricing);
            fixture.Context.Sessions.Add(fixture.Session);
            fixture.Context.Orders.Add(fixture.Order);
            await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public Task<Result<Guid>> CheckoutAsync(Guid? orderId = null, Guid? userId = null, Guid? sessionId = null,
            List<Guid>? seatIds = null, bool gold = false, bool usePoints = false)
        {
            OrderCheckoutOrchestrator checkout = new(Context, Loyalty, Payment, Locks, Gold, Publisher,
                NullLogger<OrderCheckoutOrchestrator>.Instance);
            return checkout.ProcessCheckoutAsync(userId ?? UserId, sessionId ?? Session.Id.Value,
                seatIds ?? [Seat.Id.Value], orderId ?? Order.Id.Value, gold, usePoints, "token", TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class CheckoutDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
