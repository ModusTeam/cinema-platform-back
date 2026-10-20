using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Common.Utils;
using Cinema.Application.Jobs;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Application.Orders.Services;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

// These tests describe the current checkout behavior and its payment boundaries.
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
    public async Task Cancelled_Order_Cannot_Be_Charged()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Order.MarkAsCancelled();
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
    public async Task Purchase_Event_Staging_Failure_Leaves_Payment_Outcome_Uncertain_Without_Refund()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("charged-transaction"));
        fixture.Publisher.Publish(Arg.Any<TicketPurchasedEvent>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker unavailable"));

        Result<Guid> result = await fixture.CheckoutAsync();

        result.IsFailure.Should().BeTrue();
        await fixture.Payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(o => o.Id == fixture.Order.Id,
            TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.PaymentOutcomeUnknown);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decline_Releases_Ticket_While_Provider_Exception_Requires_Reconciliation(bool providerThrows)
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        if (providerThrows)
            fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
                .Returns<Task<PaymentResult>>(_ => throw new InvalidOperationException("provider unavailable"));
        else
            fixture.Payment.ProcessPaymentAsync(50m, "UAH", "token", Arg.Any<CancellationToken>())
                .Returns(PaymentResult.Failure("declined"));

        Result<Guid> result = await fixture.CheckoutAsync();

        result.Error.Code.Should().Be(providerThrows ? "Payment.OutcomeUnknown" : "Payment.Failed");
        fixture.Order.Status.Should().Be(providerThrows ? OrderStatus.PaymentOutcomeUnknown : OrderStatus.Failed);
        fixture.Tickets.Single().TicketStatus.Should().Be(providerThrows ? TicketStatus.Reserved : TicketStatus.Released);
        await fixture.Locks.DidNotReceiveWithAnyArgs().UnlockSeatsAsync(default, default!, default, TestContext.Current.CancellationToken);
        await fixture.Publisher.DidNotReceiveWithAnyArgs().Publish(Arg.Any<TicketPurchasedEvent>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Pending_Order_Has_A_Reserved_Ticket()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Order.Status.Should().Be(OrderStatus.Pending);
        fixture.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Reserved);
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
        fixture.Order.PaidAmount.Should().Be(30m);
        fixture.Tickets.Single().PriceSnapshot.Should().Be(30m);
        fixture.Tickets.Single().IsGoldUpgraded.Should().BeTrue();
        await fixture.Payment.Received(1).ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>());
        await fixture.Gold.Received(1).CalculateAsync(Arg.Is<Session>(s => s.Id == fixture.Session.Id),
            Arg.Is<IReadOnlyCollection<GoldUpgradeTicketPrice>>(t => t.Count == 1 && t.Single().SeatId == fixture.Seat.Id.Value),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gold_Upgrade_Selects_Highest_Remaining_Ticket_Even_When_Quote_Seat_Differs()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync(ticketPrices: [40m, 70m]);
        Seat highestSeat = fixture.Seats[1];
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 10m, fixture.Seat.Id.Value, 40m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Payment.ProcessPaymentAsync(70m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("gold-highest"));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true);

        result.IsSuccess.Should().BeTrue();
        fixture.Tickets.Single(t => t.SeatId == highestSeat.Id).PriceSnapshot.Should().Be(30m);
        fixture.Tickets.Single(t => t.SeatId == highestSeat.Id).IsGoldUpgraded.Should().BeTrue();
        fixture.Tickets.Single(t => t.SeatId == fixture.Seat.Id).PriceSnapshot.Should().Be(40m);
        fixture.Order.TotalAmount.Should().Be(70m);
        fixture.Order.PaidAmount.Should().Be(70m);
        await fixture.Payment.Received(1).ProcessPaymentAsync(70m, "UAH", "token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Gold_Upgrade_Calculates_Loyalty_Discount_From_Reduced_Total()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 30m, Arg.Any<CancellationToken>())
            .Returns((true, 10, 20m));
        fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, 90, ""));
        fixture.Payment.ProcessPaymentAsync(20m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("gold-and-points"));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true, usePoints: true);

        result.IsSuccess.Should().BeTrue();
        fixture.Order.TotalAmount.Should().Be(30m);
        fixture.Order.PaidAmount.Should().Be(20m);
        fixture.Tickets.Single().PriceSnapshot.Should().Be(30m);
        await fixture.Loyalty.Received(1).CalculateDiscountAsync(fixture.UserId, 30m, Arg.Any<CancellationToken>());
        await fixture.Payment.Received(1).ProcessPaymentAsync(20m, "UAH", "token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_Gold_Checkout_Cannot_Discount_Another_Ticket_On_Retry()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync(loyaltyAllowed: false, ticketPrices: [50m, 50m]);
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Loyalty.RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));

        Result<Guid> first = await fixture.CheckoutAsync(gold: true, usePoints: true);
        first.Error.Code.Should().Be("Order.LoyaltyNotAllowed");
        Result<Guid> second = await fixture.CheckoutAsync(gold: true, usePoints: true);
        second.Error.Code.Should().Be("Order.InvalidState");
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(100m);
        fixture.Tickets.Should().OnlyContain(ticket => !ticket.IsGoldUpgraded && ticket.PriceSnapshot == 50m && ticket.TicketStatus == TicketStatus.Released);
        await fixture.Loyalty.Received(1).UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Gold_Upgrade_With_Inconsistent_Applied_Quote_Preserves_Domain_Exception()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync(ticketPrices: [30m]);
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 0m, fixture.Seat.Id.Value, 30m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));

        Func<Task> checkout = async () => await fixture.CheckoutAsync(gold: true);

        await checkout.Should().ThrowAsync<DomainException>()
            .WithMessage("No eligible ticket found for gold upgrade*");
        fixture.Order.TotalAmount.Should().Be(30m);
        fixture.Tickets.Single().IsGoldUpgraded.Should().BeFalse();
    }

    [Fact]
    public async Task Gold_Upgrade_Invalid_Discount_Does_Not_Mutate_Ticket_Or_Order()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, -10m, 60m, fixture.Seat.Id.Value, 50m, -10m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));

        Func<Task> checkout = async () => await fixture.CheckoutAsync(gold: true);

        await checkout.Should().ThrowAsync<DomainException>()
            .WithMessage("Gold discount must be positive and cannot exceed the order total.");
        fixture.Order.TotalAmount.Should().Be(50m);
        fixture.Order.PaidAmount.Should().Be(50m);
        fixture.Tickets.Single().PriceSnapshot.Should().Be(50m);
        fixture.Tickets.Single().IsGoldUpgraded.Should().BeFalse();
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders
            .SingleAsync(o => o.Id == fixture.Order.Id, TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(50m);
        (await fixture.Context.Tickets.SingleAsync(t => t.OrderId == persisted.Id, TestContext.Current.CancellationToken)).PriceSnapshot.Should().Be(50m);
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
    public async Task Unknown_Payment_Outcome_Still_Compensates_Gold_And_Retains_Points_For_Reconciliation()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 30m, Arg.Any<CancellationToken>())
            .Returns((true, 10, 20m));
        fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, 90, ""));
        fixture.Payment.ProcessPaymentAsync(20m, "UAH", "token", Arg.Any<CancellationToken>())
            .Returns<Task<PaymentResult>>(_ => throw new InvalidOperationException("unknown provider outcome"));

        fixture.Loyalty.RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        Result<Guid> result = await fixture.CheckoutAsync(gold: true, usePoints: true);

        result.Error.Code.Should().Be("Payment.OutcomeUnknown");
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.PaymentOutcomeUnknown);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        persisted.TotalAmount.Should().Be(50m);
        fixture.Tickets.Single().IsGoldUpgraded.Should().BeFalse();
        fixture.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Reserved);
        fixture.Context.Entry(persisted).Property(order => order.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new CancelExpiredOrdersJob(fixture.Context, fixture.Locks, Substitute.For<ITicketNotifier>(),
                NullLogger<CancelExpiredOrdersJob>.Instance).Process(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.PaymentOutcomeUnknown);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(fixture.UserId);
        Result cancellation = await new CancelOrderCommandHandler(fixture.Context, currentUser, fixture.Payment, fixture.Loyalty)
            .Handle(new CancelOrderCommand(fixture.Order.Id.Value), TestContext.Current.CancellationToken);
        cancellation.Error.Code.Should().Be("Order.PaymentReconciliationRequired");
        await fixture.Loyalty.Received(1).RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().RefundPointsAsync(default, default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_Rollback_Keeps_Discounted_Prices_Until_Compensation_Is_Confirmed()
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
        fixture.Order.PaidAmount.Should().Be(30m);
        fixture.Tickets.Single().PriceSnapshot.Should().Be(30m);
        fixture.Tickets.Single().IsGoldUpgraded.Should().BeTrue();
        await fixture.Loyalty.Received(1).RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders
            .SingleAsync(o => o.Id == fixture.Order.Id, TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(30m);
        persisted.PaidAmount.Should().Be(30m);
        (await fixture.Context.Tickets.SingleAsync(t => t.OrderId == persisted.Id, TestContext.Current.CancellationToken)).PriceSnapshot.Should().Be(30m);
        (await fixture.Context.Tickets.SingleAsync(t => t.OrderId == persisted.Id, TestContext.Current.CancellationToken)).IsGoldUpgraded.Should().BeTrue();
    }

    [Theory]
    [InlineData("decline")]
    [InlineData("deduct")]
    [InlineData("discount")]
    [InlineData("confirmation")]
    [InlineData("exception")]
    [InlineData("cancellation")]
    public async Task Every_Unsuccessful_Gold_Checkout_Compensates_And_Restores_Prices(string failure)
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Loyalty.RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 30m, Arg.Any<CancellationToken>()).Returns((true, 10, 20m));
        fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((false, 0, "deduct failed"));
        fixture.Payment.ProcessPaymentAsync(Arg.Any<decimal>(), "UAH", "token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure("declined"));
        if (failure == "discount")
            fixture.Loyalty.CalculateDiscountAsync(fixture.UserId, 30m, Arg.Any<CancellationToken>())
                .Returns<Task<(bool, int, decimal)>>(_ => throw new InvalidOperationException("offline"));
        if (failure == "confirmation")
        {
            fixture.Payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("charged"));
            fixture.FailBeforeConfirmationSave = true;
        }
        if (failure is "exception" or "cancellation")
            fixture.Loyalty.DeductPointsAsync(fixture.UserId, 10, fixture.Order.Id.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns<Task<(bool, int, string)>>(_ => throw failure switch
                {
                    "cancellation" => new OperationCanceledException(),
                    _ => new InvalidOperationException("after Use")
                });
        try { await fixture.CheckoutAsync(gold: true, usePoints: failure is "deduct" or "discount" or "exception" or "cancellation"); }
        catch (Exception) when (failure is "exception" or "cancellation") { }

        await fixture.Loyalty.Received(1).RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value,
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(50m);
        persisted.PaidAmount.Should().Be(50m);
        Ticket ticket = await fixture.Context.Tickets.SingleAsync(TestContext.Current.CancellationToken);
        ticket.PriceSnapshot.Should().Be(50m);
        ticket.IsGoldUpgraded.Should().BeFalse();
        ticket.TicketStatus.Should().Be(failure == "confirmation" ? TicketStatus.Reserved : TicketStatus.Released);
    }

    [Fact]
    public async Task Retry_After_Failed_Rollback_Recovers_Before_Rejecting_Failed_Order()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Loyalty.RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>())
            .Returns((false, "offline"), (true, ""));
        fixture.Payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Failure("declined"));
        await fixture.CheckoutAsync(gold: true);
        fixture.Order.TotalAmount.Should().Be(30m);
        fixture.Context.ChangeTracker.Clear();

        Result<Guid> retry = await fixture.CheckoutAsync();

        retry.IsFailure.Should().BeTrue();
        Order persisted = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(50m);
        (await fixture.Context.Tickets.SingleAsync(TestContext.Current.CancellationToken)).IsGoldUpgraded.Should().BeFalse();
        await fixture.Payment.Received(1).ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Caller_Cancellation_After_Use_Cannot_Cancel_Compensation()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>())
            .Returns(_ => { caller.Cancel(); return (true, ""); });
        fixture.Loyalty.RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));

        Func<Task> checkout = async () => await fixture.CheckoutAsync(gold: true, ct: caller.Token);
        await checkout.Should().ThrowAsync<OperationCanceledException>();

        await fixture.Loyalty.Received(1).RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value,
            Arg.Is<CancellationToken>(token => !token.IsCancellationRequested));
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        persisted.TotalAmount.Should().Be(50m);
    }

    [Fact]
    public async Task Lost_Local_Confirmation_Response_Does_Not_Refund_A_Committed_Gold_Order()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.FailAfterConfirmationSave = true;
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("charged"));

        Result<Guid> result = await fixture.CheckoutAsync(gold: true);

        result.IsSuccess.Should().BeTrue();
        fixture.Context.ChangeTracker.Clear();
        Order persisted = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Paid);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Completed);
        await fixture.Payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, TestContext.Current.CancellationToken);
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().RollbackGoldUpgradeAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Unresolved_Use_Preserves_Ambiguous_Journal_And_Recovers_On_Retry()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>())
            .Returns<Task<(bool, string)>>(_ => throw new TimeoutException("Use outcome unknown"));
        fixture.Loyalty.RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        Func<Task> checkout = async () => await fixture.CheckoutAsync(gold: true);
        await checkout.Should().ThrowAsync<TimeoutException>();
        fixture.Context.ChangeTracker.Clear();
        Order pending = await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        pending.GoldCheckoutState.Should().Be(GoldCheckoutState.CompensationPending);
        pending.GoldUseConfirmed.Should().BeFalse();
        pending.TotalAmount.Should().Be(50m);

        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        (await fixture.CheckoutAsync()).Error.Code.Should().Be("Order.GoldRecoveryRequired");

        fixture.Context.ChangeTracker.Clear();
        (await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken))
            .GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        await fixture.Loyalty.Received(3).UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
        await fixture.Loyalty.Received(1).RollbackGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
        await fixture.Payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Successful_Gold_Checkout_Replay_Does_Not_Consume_Or_Discount_Again()
    {
        await using CheckoutFixture fixture = await CheckoutFixture.CreateAsync();
        fixture.Gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, fixture.Seat.Id.Value, 50m, 30m, null)));
        fixture.Loyalty.UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        fixture.Payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("gold-paid"));
        (await fixture.CheckoutAsync(gold: true)).IsSuccess.Should().BeTrue();
        fixture.Context.ChangeTracker.Clear();
        (await fixture.CheckoutAsync(gold: true)).IsSuccess.Should().BeTrue();
        (await fixture.Context.Orders.SingleAsync(TestContext.Current.CancellationToken)).TotalAmount.Should().Be(30m);
        await fixture.Loyalty.Received(1).UseGoldUpgradeAsync(fixture.UserId, fixture.Order.Id.Value, Arg.Any<CancellationToken>());
        await fixture.Payment.Received(1).ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>());
        await fixture.Loyalty.DidNotReceiveWithAnyArgs().RollbackGoldUpgradeAsync(default, default, TestContext.Current.CancellationToken);
    }

    private sealed class CheckoutFixture : IAsyncDisposable
    {
        public ApplicationDbContext Context { get; }
        public Guid UserId { get; } = Guid.NewGuid();
        public Session Session { get; private set; } = null!;
        public Seat Seat { get; private set; } = null!;
        public List<Seat> Seats { get; } = [];
        public Order Order { get; private set; } = null!;
        public List<Ticket> Tickets => Context.Tickets.Where(t => t.OrderId == Order.Id).ToList();
        public IPaymentService Payment { get; } = Substitute.For<IPaymentService>();
        public ILoyaltyService Loyalty { get; } = Substitute.For<ILoyaltyService>();
        public ISeatLockingService Locks { get; } = Substitute.For<ISeatLockingService>();
        public IGoldUpgradePricingService Gold { get; } = Substitute.For<IGoldUpgradePricingService>();
        public IPublishEndpoint Publisher { get; } = Substitute.For<IPublishEndpoint>();

        public bool FailBeforeConfirmationSave { set => ((CheckoutDbContext)Context).FailBeforeConfirmationSave = value; }
        public bool FailAfterConfirmationSave { set => ((CheckoutDbContext)Context).FailAfterConfirmationSave = value; }

        private CheckoutFixture()
        {
            DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
            Context = new CheckoutDbContext(options);
        }

        public static async Task<CheckoutFixture> CreateAsync(bool loyaltyAllowed = true, decimal[]? ticketPrices = null)
        {
            CheckoutFixture fixture = new();
            Hall hall = Hall.Create(EntityId<Hall>.New(), "Checkout test hall");
            SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Premium", null);
            fixture.Seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
            fixture.Seats.Add(fixture.Seat);
            decimal[] prices = ticketPrices ?? [50m];
            for (int index = 1; index < prices.Length; index++)
                fixture.Seats.Add(Seat.New(EntityId<Seat>.New(), "A", index + 1, 1, index + 1,
                    SeatStatus.Active, hall.Id, seatType.Id));
            hall.ApplyLayout(fixture.Seats);
            Pricing pricing = Pricing.New(EntityId<Pricing>.New(), "Checkout test pricing");
            fixture.Session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
                DateTime.UtcNow.AddDays(2).AddHours(2), EntityId<Movie>.New(), hall.Id, pricing.Id, loyaltyAllowed);
            Dictionary<EntityId<Seat>, decimal> seatPrices = fixture.Seats
                .Select((seat, index) => (seat, price: prices[index]))
                .ToDictionary(item => item.seat.Id, item => item.price);
            fixture.Order = OrderTicketFixture.CreateAndTrack(fixture.Context, fixture.UserId, fixture.Session, fixture.Seats, seatPrices, TicketStatus.Reserved);
            fixture.Context.Halls.Add(hall);
            fixture.Context.SeatTypes.Add(seatType);
            fixture.Context.Pricings.Add(pricing);
            fixture.Context.Sessions.Add(fixture.Session);
            await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return fixture;
        }

        public Task<Result<Guid>> CheckoutAsync(Guid? orderId = null, Guid? userId = null, Guid? sessionId = null,
            List<Guid>? seatIds = null, bool gold = false, bool usePoints = false, CancellationToken? ct = null)
        {
            OrderCheckoutOrchestrator checkout = new(Context, Loyalty, Payment, Locks, Gold, Publisher,
                NullLogger<OrderCheckoutOrchestrator>.Instance);
            return checkout.ProcessCheckoutAsync(userId ?? UserId, sessionId ?? Session.Id.Value,
                seatIds ?? [Seat.Id.Value], orderId ?? Order.Id.Value, gold, usePoints, "token", ct ?? TestContext.Current.CancellationToken);
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class CheckoutDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        public bool FailBeforeConfirmationSave { get; set; }
        public bool FailAfterConfirmationSave { get; set; }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailBeforeConfirmationSave && Orders.Local.Any(order => order.Status == OrderStatus.Paid))
            {
                FailBeforeConfirmationSave = false;
                throw new IOException("Confirmation failed before commit");
            }
            int count = await base.SaveChangesAsync(cancellationToken);
            if (FailAfterConfirmationSave && Orders.Local.Any(order => order.Status == OrderStatus.Paid))
            {
                FailAfterConfirmationSave = false;
                throw new IOException("Confirmation committed but response lost");
            }
            return count;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
