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
    public void Order_User_Relationship_Keeps_Foreign_Key_Without_Domain_Navigation()
    {
        using ApplicationDbContext context = CreateContext();
        Microsoft.EntityFrameworkCore.Metadata.IEntityType orderType = context.Model.FindEntityType(typeof(Order))!;
        Microsoft.EntityFrameworkCore.Metadata.IForeignKey userForeignKey = orderType.GetForeignKeys()
            .Single(fk => fk.PrincipalEntityType.ClrType == typeof(User));

        orderType.FindProperty(nameof(Order.UserId)).Should().NotBeNull();
        userForeignKey.Properties.Should().ContainSingle(property => property.Name == nameof(Order.UserId));
        userForeignKey.DependentToPrincipal.Should().BeNull();
        userForeignKey.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        orderType.GetIndexes().Should().Contain(index =>
            index.Properties.Count == 1 && index.Properties[0].Name == nameof(Order.UserId));
    }

    [Fact]
    public void Identity_Entities_Keep_Their_Keys_And_User_Relationships()
    {
        using ApplicationDbContext context = CreateContext();
        Microsoft.EntityFrameworkCore.Metadata.IEntityType userType = context.Model.FindEntityType(typeof(User))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType refreshTokenType = context.Model.FindEntityType(typeof(RefreshToken))!;
        Microsoft.EntityFrameworkCore.Metadata.IEntityType seatLockType = context.Model.FindEntityType(typeof(SeatLock))!;

        userType.FindPrimaryKey()!.Properties.Should().ContainSingle(property => property.Name == nameof(User.Id));
        userType.FindProperty(nameof(User.PasswordHash)).Should().NotBeNull();
        userType.FindProperty(nameof(User.SecurityStamp)).Should().NotBeNull();
        refreshTokenType.GetForeignKeys().Should().ContainSingle(foreignKey =>
            foreignKey.PrincipalEntityType == userType &&
            foreignKey.Properties.Single().Name == nameof(RefreshToken.UserId) &&
            foreignKey.DependentToPrincipal != null &&
            foreignKey.DependentToPrincipal.Name == nameof(RefreshToken.User));
        seatLockType.GetForeignKeys().Should().ContainSingle(foreignKey =>
            foreignKey.PrincipalEntityType == userType &&
            foreignKey.Properties.Single().Name == nameof(SeatLock.UserId) &&
            foreignKey.DependentToPrincipal != null &&
            foreignKey.DependentToPrincipal.Name == nameof(SeatLock.User));
    }

    [Fact]
    public void Shared_Model_Loads_Order_And_Ticket_Configurations_Once()
    {
        using ApplicationDbContext context = CreateContext();
        Microsoft.EntityFrameworkCore.Metadata.IEntityType orderType = context.Model.GetEntityTypes()
            .Single(entity => entity.ClrType == typeof(Order));
        Microsoft.EntityFrameworkCore.Metadata.IEntityType ticketType = context.Model.GetEntityTypes()
            .Single(entity => entity.ClrType == typeof(Ticket));

        orderType.GetForeignKeys().Should().HaveCount(2);
        ticketType.GetForeignKeys().Should().HaveCount(3);
        ticketType.GetForeignKeys().Single(foreignKey => foreignKey.PrincipalEntityType == orderType)
            .DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        ticketType.GetQueryFilter().Should().NotBeNull();
        ticketType.GetIndexes().Should().ContainSingle(index => index.IsUnique &&
            index.GetFilter() == "ticket_status IN (0, 1)");
    }

    [Fact]
    public void Separate_Creation_Uses_Seat_Prices_For_Tickets_And_Order_Totals()
    {
        (Session session, Seat first, Seat second) = Seats();
        Guid userId = Guid.NewGuid();

        (Order order, List<Ticket> tickets) = OrderTicketFixture.Create(userId, session, [first, second], new()
        {
            [first.Id] = 25m,
            [second.Id] = 40m
        });

        order.Status.Should().Be(OrderStatus.Pending);
        order.UserId.Should().Be(userId);
        order.SessionId.Should().Be(session.Id);
        order.TotalAmount.Should().Be(65m);
        order.PaidAmount.Should().Be(65m);
        tickets.Should().HaveCount(2);
        tickets.Should().OnlyContain(t => t.OrderId == order.Id &&
            t.SessionId == session.Id && t.TicketStatus == TicketStatus.Valid);
        tickets.Should().ContainSingle(t => t.SeatId == first.Id && t.PriceSnapshot == 25m);
        tickets.Should().ContainSingle(t => t.SeatId == second.Id && t.PriceSnapshot == 40m);
    }

    [Fact]
    public void CreateWithoutTickets_Rejects_Foreign_Or_Inactive_Seat_And_Missing_Price()
    {
        (Session session, Seat first, Seat second) = Seats();
        Seat foreign = Seat.New(EntityId<Seat>.New(), "B", 1, 1, 1, SeatStatus.Active,
            EntityId<Hall>.New(), first.SeatTypeId);
        second.SetStatus(SeatStatus.Maintenance);

        Action foreignSeat = () => Order.CreateWithoutTickets(Guid.NewGuid(), session, [foreign], new() { [foreign.Id] = 25m });
        Action inactiveSeat = () => Order.CreateWithoutTickets(Guid.NewGuid(), session, [second], new() { [second.Id] = 25m });
        Action missingPrice = () => Order.CreateWithoutTickets(Guid.NewGuid(), session, [first], new());

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
        using ApplicationDbContext context = CreateContext();
        context.Orders.Add(order);
        context.ChangeTracker.Entries<Ticket>().Should().BeEmpty();

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
        (Order order, List<Ticket> tickets) = OrderTicketFixture.Create(Guid.NewGuid(), session, [first, second], new()
        {
            [first.Id] = 40m,
            [second.Id] = 70m
        });

        Ticket secondTicket = tickets.Single(t => t.SeatId == second.Id);
        secondTicket.ApplyGoldUpgrade(30m);
        order.ApplyGoldDiscount(40m);

        tickets.Single(t => t.SeatId == second.Id).PriceSnapshot.Should().Be(30m);
        tickets.Single(t => t.SeatId == second.Id).IsGoldUpgraded.Should().BeTrue();
        tickets.Single(t => t.SeatId == first.Id).PriceSnapshot.Should().Be(40m);
        order.TotalAmount.Should().Be(70m);
        order.PaidAmount.Should().Be(70m);

        // Current behavior allows a second upgrade on another eligible ticket.
        Ticket firstTicket = tickets.Single(t => t.SeatId == first.Id);
        firstTicket.ApplyGoldUpgrade(30m);
        order.ApplyGoldDiscount(10m);
        order.TotalAmount.Should().Be(60m);
        order.PaidAmount.Should().Be(60m);
        tickets.Should().OnlyContain(t => t.IsGoldUpgraded && t.PriceSnapshot == 30m);
        Action thirdUpgrade = () => secondTicket.ApplyGoldUpgrade(30m);
        thirdUpgrade.Should().Throw<DomainException>().WithMessage("Ticket is already gold upgraded.");
        order.TotalAmount.Should().Be(60m);
    }

    [Fact]
    public void Gold_Discount_Clamps_Paid_Amount_After_Loyalty_Discount()
    {
        (Session session, Seat first, Seat second) = Seats();
        (Order order, List<Ticket> tickets) = OrderTicketFixture.Create(Guid.NewGuid(), session, [first, second], new()
        {
            [first.Id] = 20m,
            [second.Id] = 30m
        });

        order.ApplyLoyaltyDiscount(5);
        order.ApplyGoldDiscount(10m);

        order.TotalAmount.Should().Be(40m);
        order.PaidAmount.Should().Be(40m);
        tickets.Should().OnlyContain(t => !t.IsGoldUpgraded);
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

        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await new DispatchDomainEventsInterceptor(publisher).DispatchDomainEvents(context, cancellationToken);

        order.DomainEvents.Should().BeEmpty();
        await publisher.Received(1).Publish(Arg.Is<OrderPaidEvent>(e => e.Order == order), cancellationToken);
        await publisher.Received(1).Publish(Arg.Is<DomainEventNotification<OrderPaidEvent>>(n => n.DomainEvent.Order == order), cancellationToken);
        await new DispatchDomainEventsInterceptor(publisher).DispatchDomainEvents(context, cancellationToken);
        publisher.ReceivedCalls().Count().Should().Be(2);
    }

    [Fact]
    public async Task Domain_Event_Dispatch_Propagates_Cancellation_To_Blocked_Publisher()
    {
        IPublisher publisher = Substitute.For<IPublisher>();
        await using ApplicationDbContext context = CreateContext();
        Order order = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), EntityId<Session>.New());
        context.Orders.Add(order);
        order.MarkAsPaid("transaction");
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        publisher.Publish(Arg.Any<OrderPaidEvent>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<CancellationToken>(1));
            });
        using CancellationTokenSource cancellation = new();

        Task dispatch = new DispatchDomainEventsInterceptor(publisher)
            .DispatchDomainEvents(context, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        await FluentActions.Awaiting(() => dispatch).Should().ThrowAsync<OperationCanceledException>();
        await publisher.Received(1).Publish(Arg.Any<OrderPaidEvent>(), cancellation.Token);
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
