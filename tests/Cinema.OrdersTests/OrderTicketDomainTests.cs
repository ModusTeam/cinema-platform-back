using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Events;
using Cinema.Domain.Exceptions;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class OrderTicketDomainTests
{
    [Fact]
    public void Create_Uses_Seat_Prices_For_Tickets_And_Order_Totals()
    {
        (Session session, Seat first, Seat second) = Seats();
        Guid userId = Guid.NewGuid();

        Order order = Order.Create(userId, session, [first, second], new()
        {
            [first.Id] = 25m,
            [second.Id] = 40m
        });

        order.Status.Should().Be(OrderStatus.Pending);
        order.UserId.Should().Be(userId);
        order.SessionId.Should().Be(session.Id);
        order.TotalAmount.Should().Be(65m);
        order.PaidAmount.Should().Be(65m);
        order.Tickets.Should().HaveCount(2);
        order.Tickets.Should().OnlyContain(t => t.OrderId == order.Id &&
            t.SessionId == session.Id && t.TicketStatus == TicketStatus.Valid);
        order.Tickets.Should().ContainSingle(t => t.SeatId == first.Id && t.PriceSnapshot == 25m);
        order.Tickets.Should().ContainSingle(t => t.SeatId == second.Id && t.PriceSnapshot == 40m);
    }

    [Fact]
    public void Create_Rejects_Foreign_Or_Inactive_Seat_And_Missing_Price()
    {
        (Session session, Seat first, Seat second) = Seats();
        Seat foreign = Seat.New(EntityId<Seat>.New(), "B", 1, 1, 1, SeatStatus.Active,
            EntityId<Hall>.New(), first.SeatTypeId);
        second.SetStatus(SeatStatus.Maintenance);

        Action foreignSeat = () => Order.Create(Guid.NewGuid(), session, [foreign], new() { [foreign.Id] = 25m });
        Action inactiveSeat = () => Order.Create(Guid.NewGuid(), session, [second], new() { [second.Id] = 25m });
        Action missingPrice = () => Order.Create(Guid.NewGuid(), session, [first], new());

        foreignSeat.Should().Throw<DomainException>().WithMessage("Seats belong to a different hall.");
        inactiveSeat.Should().Throw<DomainException>().WithMessage("One or more seats are not active.");
        missingPrice.Should().Throw<DomainException>().WithMessage("Price not found for seat *");
    }

    [Fact]
    public void CreateWithoutTickets_Preserves_Validation_And_Order_Totals()
    {
        (Session session, Seat first, Seat second) = Seats();
        Dictionary<EntityId<Seat>, decimal> prices = new() { [first.Id] = 25m, [second.Id] = 40m };
        Order order = Order.CreateWithoutTickets(Guid.NewGuid(), session, [first, second], prices);

        order.Status.Should().Be(OrderStatus.Pending);
        order.TotalAmount.Should().Be(65m);
        order.PaidAmount.Should().Be(65m);
        order.Tickets.Should().BeEmpty();

        Seat foreign = Seat.New(EntityId<Seat>.New(), "B", 1, 1, 1, SeatStatus.Active,
            EntityId<Hall>.New(), first.SeatTypeId);
        second.SetStatus(SeatStatus.Maintenance);
        Action foreignSeat = () => Order.CreateWithoutTickets(Guid.NewGuid(), session, [foreign], prices);
        Action inactiveSeat = () => Order.CreateWithoutTickets(Guid.NewGuid(), session, [second], prices);
        Action missingPrice = () => Order.CreateWithoutTickets(Guid.NewGuid(), session, [first], new());

        foreignSeat.Should().Throw<DomainException>().WithMessage("Seats belong to a different hall.");
        inactiveSeat.Should().Throw<DomainException>().WithMessage("One or more seats are not active.");
        missingPrice.Should().Throw<DomainException>().WithMessage("Price not found for seat *");
    }

    [Fact]
    public void Gold_Discount_Adjusts_Totals_For_Separately_Upgraded_Tickets()
    {
        (Session session, Seat first, Seat second) = Seats();
        Order order = Order.Create(Guid.NewGuid(), session, [first, second], new()
        {
            [first.Id] = 40m,
            [second.Id] = 70m
        });

        Ticket secondTicket = order.Tickets.Single(t => t.SeatId == second.Id);
        secondTicket.ApplyGoldUpgrade(30m);
        order.ApplyGoldDiscount(40m);

        order.Tickets.Single(t => t.SeatId == second.Id).PriceSnapshot.Should().Be(30m);
        order.Tickets.Single(t => t.SeatId == second.Id).IsGoldUpgraded.Should().BeTrue();
        order.Tickets.Single(t => t.SeatId == first.Id).PriceSnapshot.Should().Be(40m);
        order.TotalAmount.Should().Be(70m);
        order.PaidAmount.Should().Be(70m);

        // Current behavior allows a second upgrade on another eligible ticket.
        Ticket firstTicket = order.Tickets.Single(t => t.SeatId == first.Id);
        firstTicket.ApplyGoldUpgrade(30m);
        order.ApplyGoldDiscount(10m);
        order.TotalAmount.Should().Be(60m);
        order.PaidAmount.Should().Be(60m);
        order.Tickets.Should().OnlyContain(t => t.IsGoldUpgraded && t.PriceSnapshot == 30m);
        Action thirdUpgrade = () => secondTicket.ApplyGoldUpgrade(30m);
        thirdUpgrade.Should().Throw<DomainException>().WithMessage("Ticket is already gold upgraded.");
        order.TotalAmount.Should().Be(60m);
    }

    [Fact]
    public void Gold_Discount_Clamps_Paid_Amount_After_Loyalty_Discount()
    {
        (Session session, Seat first, Seat second) = Seats();
        Order order = Order.Create(Guid.NewGuid(), session, [first, second], new()
        {
            [first.Id] = 20m,
            [second.Id] = 30m
        });

        order.ApplyLoyaltyDiscount(5);
        order.ApplyGoldDiscount(10m);

        order.TotalAmount.Should().Be(40m);
        order.PaidAmount.Should().Be(40m);
        order.Tickets.Should().OnlyContain(t => !t.IsGoldUpgraded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(51)]
    public void Gold_Discount_Rejects_Invalid_Adjustment_Without_Changing_Amounts(decimal discount)
    {
        Order order = Order.New(EntityId<Order>.New(), 50m, Guid.NewGuid(), EntityId<Session>.New());
        order.ApplyLoyaltyDiscount(10);

        Action apply = () => order.ApplyGoldDiscount(discount);

        apply.Should().Throw<DomainException>()
            .WithMessage("Gold discount must be positive and cannot exceed the order total.");
        order.TotalAmount.Should().Be(50m);
        order.PaidAmount.Should().Be(40m);
    }

    [Fact]
    public void Ticket_Use_And_Refund_Rules_Preserve_Current_States()
    {
        EntityId<Order> orderId = EntityId<Order>.New();
        EntityId<Session> sessionId = EntityId<Session>.New();
        Ticket used = Ticket.New(EntityId<Ticket>.New(), 10m, TicketStatus.Valid, orderId, sessionId, EntityId<Seat>.New());
        Ticket refunded = Ticket.New(EntityId<Ticket>.New(), 10m, TicketStatus.Valid, orderId, sessionId, EntityId<Seat>.New());

        used.MarkAsUsed();
        used.TicketStatus.Should().Be(TicketStatus.Used);
        ((Action)used.MarkAsUsed).Should().Throw<DomainException>();
        ((Action)used.MarkAsRefunded).Should().Throw<DomainException>();
        used.TicketStatus.Should().Be(TicketStatus.Used);

        refunded.MarkAsRefunded();
        refunded.MarkAsRefunded(); // Current behavior is idempotent.
        refunded.TicketStatus.Should().Be(TicketStatus.Refunded);
        ((Action)refunded.MarkAsUsed).Should().Throw<DomainException>();
    }

    [Fact]
    public async Task Paid_Transition_Dispatches_Direct_And_Wrapped_Notifications_Once()
    {
        IPublisher publisher = Substitute.For<IPublisher>();
        await using ApplicationDbContext context = CreateContext();
        Order order = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), EntityId<Session>.New());
        context.Orders.Add(order);
        order.MarkAsPaid("transaction");
        order.MarkAsPaid("duplicate");

        await new DispatchDomainEventsInterceptor(publisher).DispatchDomainEvents(context);

        order.DomainEvents.Should().BeEmpty();
        await publisher.Received(1).Publish(Arg.Is<OrderPaidEvent>(e => e.Order == order), Arg.Any<CancellationToken>());
        await publisher.Received(1).Publish(Arg.Is<DomainEventNotification<OrderPaidEvent>>(n => n.DomainEvent.Order == order), Arg.Any<CancellationToken>());
        await new DispatchDomainEventsInterceptor(publisher).DispatchDomainEvents(context);
        publisher.ReceivedCalls().Count().Should().Be(2);
    }

    private static (Session Session, Seat First, Seat Second) Seats()
    {
        EntityId<Hall> hallId = EntityId<Hall>.New();
        EntityId<SeatType> seatTypeId = EntityId<SeatType>.New();
        Seat first = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hallId, seatTypeId);
        Seat second = Seat.New(EntityId<Seat>.New(), "A", 2, 1, 2, SeatStatus.Active, hallId, seatTypeId);
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            EntityId<Movie>.New(), hallId, EntityId<Pricing>.New());
        return (session, first, second);
    }

    private static ApplicationDbContext CreateContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new OrdersTestDbContext(options);
    }

    private sealed class OrdersTestDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
