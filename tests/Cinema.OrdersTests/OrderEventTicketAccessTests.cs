using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Application.Orders.EventHandlers;
using Cinema.Application.Jobs;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Events;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

// Paid notifications intentionally call the existing overload without a cancellation token.
#pragma warning disable xUnit1051

namespace Cinema.OrdersTests;

public class OrderEventTicketAccessTests
{
    [Fact]
    public async Task Paid_Unlocks_Only_Its_Tickets_And_Notifies_After_Unlock_Failure()
    {
        await using ApplicationDbContext context = CreateContext();
        (Order order, Ticket[] tickets, Ticket unrelated) = await SeedAsync(context);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        locks.UnlockSeatAsync(order.SessionId.Value, tickets[0].SeatId.Value, order.UserId)
            .Returns(Task.FromException<Result>(new InvalidOperationException("Redis unavailable")));

        await new OrderPaidEventHandler(context, notifier, locks, NullLogger<OrderPaidEventHandler>.Instance)
            .Handle(new DomainEventNotification<OrderPaidEvent>(new OrderPaidEvent(order)), CancellationToken.None);

        await locks.Received(1).UnlockSeatAsync(order.SessionId.Value, tickets[0].SeatId.Value, order.UserId);
        await locks.Received(1).UnlockSeatAsync(order.SessionId.Value, tickets[1].SeatId.Value, order.UserId);
        await locks.DidNotReceive().UnlockSeatAsync(Arg.Any<Guid>(), unrelated.SeatId.Value, Arg.Any<Guid>());
        await notifier.Received(1).NotifyOrderCompleted(order.UserId, order.Id.Value);
    }

    [Fact]
    public async Task Failed_Unlocks_Its_Seats_And_Propagates_Unlock_Failure()
    {
        await using ApplicationDbContext context = CreateContext();
        (Order order, Ticket[] tickets, Ticket unrelated) = await SeedAsync(context);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.UnlockSeatsAsync(Arg.Any<Guid>(), Arg.Any<IEnumerable<Guid>>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Result>(new InvalidOperationException("Redis unavailable")));

        Func<Task> handle = () => new OrderFailedEventHandler(context, locks, NullLogger<OrderFailedEventHandler>.Instance)
            .Handle(new DomainEventNotification<OrderFailedDomainEvent>(new OrderFailedDomainEvent(order)), CancellationToken.None);

        await handle.Should().ThrowAsync<InvalidOperationException>();
        await locks.Received(1).UnlockSeatsAsync(order.SessionId.Value,
            Arg.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(tickets.Select(t => t.SeatId.Value)) &&
                !ids.Contains(unrelated.SeatId.Value)), order.UserId, CancellationToken.None);
    }

    [Fact]
    public async Task Cancelled_Notifies_Each_Seat_Despite_One_Notification_Failure()
    {
        await using ApplicationDbContext context = CreateContext();
        (Order order, Ticket[] tickets, Ticket unrelated) = await SeedAsync(context);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        notifier.NotifySeatUnlockedAsync(tickets[0].SessionId.Value, tickets[0].SeatId.Value, CancellationToken.None)
            .Returns(Task.FromException(new InvalidOperationException("SignalR unavailable")));

        await new OrderCancelledEventHandler(context, locks, notifier, NullLogger<OrderCancelledEventHandler>.Instance)
            .Handle(new DomainEventNotification<OrderCancelledDomainEvent>(new OrderCancelledDomainEvent(order)), CancellationToken.None);

        await locks.Received(1).UnlockSeatsAsync(order.SessionId.Value,
            Arg.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(tickets.Select(t => t.SeatId.Value))),
            order.UserId, CancellationToken.None);
        foreach (Ticket ticket in tickets)
            await notifier.Received(1).NotifySeatUnlockedAsync(ticket.SessionId.Value, ticket.SeatId.Value, CancellationToken.None);
        await notifier.DidNotReceive().NotifySeatUnlockedAsync(Arg.Any<Guid>(), unrelated.SeatId.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Failed_With_No_Tickets_Does_Not_Unlock()
    {
        await using ApplicationDbContext context = CreateContext();
        Order order = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), EntityId<Session>.New());
        context.Orders.Add(order);
        await context.SaveChangesAsync(CancellationToken.None);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();

        await new OrderFailedEventHandler(context, locks, NullLogger<OrderFailedEventHandler>.Instance)
            .Handle(new DomainEventNotification<OrderFailedDomainEvent>(new OrderFailedDomainEvent(order)), CancellationToken.None);

        await locks.DidNotReceiveWithAnyArgs().UnlockSeatsAsync(default, default!, default, default);
    }

    [Fact]
    public async Task Paid_Includes_Newly_Added_Ticket_Before_First_Save()
    {
        await using ApplicationDbContext context = CreateContext();
        Order order = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), EntityId<Session>.New());
        Ticket ticket = Ticket.New(EntityId<Ticket>.New(), 20m, TicketStatus.Valid,
            order.Id, order.SessionId, EntityId<Seat>.New());
        context.Orders.Add(order);
        context.Tickets.Add(ticket);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();

        await new OrderPaidEventHandler(context, notifier, locks, NullLogger<OrderPaidEventHandler>.Instance)
            .Handle(new DomainEventNotification<OrderPaidEvent>(new OrderPaidEvent(order)), TestContext.Current.CancellationToken);

        await locks.Received(1).UnlockSeatAsync(order.SessionId.Value, ticket.SeatId.Value, order.UserId);
        await notifier.Received(1).NotifyOrderCompleted(order.UserId, order.Id.Value);
    }

    [Fact]
    public async Task Expiration_Processes_Multiple_Batches_Without_Changing_Other_Orders_Or_Tickets()
    {
        await using ApplicationDbContext context = CreateContext();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Expiration hall");
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        EntityId<Session> sessionId = EntityId<Session>.New();
        Seat[] seats = Enumerable.Range(1, 103)
            .Select(number => Seat.New(EntityId<Seat>.New(), "A", number, 1, number,
                SeatStatus.Active, hall.Id, seatType.Id)).ToArray();
        hall.ApplyLayout(seats);
        List<Order> expired = new();
        List<Ticket> tickets = new();
        for (int number = 1; number <= 103; number++)
        {
            Seat seat = seats[number - 1];
            Order order = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), sessionId);
            if (number == 102)
                order.MarkAsPaid("paid");
            if (number != 103)
                context.Entry(order).Property(o => o.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            if (number <= 101)
                expired.Add(order);
            context.Orders.Add(order);
            Ticket ticket = Ticket.New(EntityId<Ticket>.New(), 20m, TicketStatus.Valid,
                order.Id, sessionId, seat.Id);
            tickets.Add(ticket);
            context.Tickets.Add(ticket);
        }
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        context.ClearChangeTracker();
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();

        await new CancelExpiredOrdersJob(context, locks, NullLogger<CancelExpiredOrdersJob>.Instance)
            .Process(TestContext.Current.CancellationToken);

        context.ChangeTracker.Entries().Should().BeEmpty();
        (await context.Orders.CountAsync(o => o.Status == OrderStatus.Cancelled,
            TestContext.Current.CancellationToken)).Should().Be(101);
        (await context.Orders.CountAsync(o => o.Status == OrderStatus.Paid,
            TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.Orders.CountAsync(o => o.Status == OrderStatus.Pending,
            TestContext.Current.CancellationToken)).Should().Be(1);
        (await context.Tickets.CountAsync(t => t.TicketStatus == TicketStatus.Valid,
            TestContext.Current.CancellationToken)).Should().Be(103);
        foreach (Order order in expired)
        {
            Ticket ticket = tickets.Single(t => t.OrderId == order.Id);
            await locks.Received(1).UnlockSeatAsync(sessionId.Value, ticket.SeatId.Value, order.UserId,
                TestContext.Current.CancellationToken);
        }
        await locks.DidNotReceive().UnlockSeatAsync(Arg.Any<Guid>(), tickets[101].SeatId.Value,
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await locks.DidNotReceive().UnlockSeatAsync(Arg.Any<Guid>(), tickets[102].SeatId.Value,
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    private static async Task<(Order Order, Ticket[] Tickets, Ticket Unrelated)> SeedAsync(ApplicationDbContext context)
    {
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Event hall");
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat[] seats = Enumerable.Range(1, 3)
            .Select(number => Seat.New(EntityId<Seat>.New(), "A", number, 1, number,
                SeatStatus.Active, hall.Id, seatType.Id)).ToArray();
        hall.ApplyLayout(seats.ToList());
        Order order = Order.New(EntityId<Order>.New(), 40m, Guid.NewGuid(), EntityId<Session>.New());
        Order other = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), order.SessionId);
        Ticket[] tickets = seats.Take(2).Select(seat => Ticket.New(EntityId<Ticket>.New(), 20m,
            TicketStatus.Valid, order.Id, order.SessionId, seat.Id)).ToArray();
        Ticket unrelated = Ticket.New(EntityId<Ticket>.New(), 20m, TicketStatus.Valid,
            other.Id, other.SessionId, seats[2].Id);
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Orders.AddRange(order, other);
        context.Tickets.AddRange(tickets.Append(unrelated));
        await context.SaveChangesAsync(CancellationToken.None);
        context.ClearChangeTracker();
        Order unloaded = await context.Orders.SingleAsync(o => o.Id == order.Id);
        unloaded.Tickets.Should().BeEmpty();
        return (unloaded, tickets, unrelated);
    }

    private static ApplicationDbContext CreateContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new EventTestDbContext(options);
    }

    private sealed class EventTestDbContext(DbContextOptions<ApplicationDbContext> options)
        : ApplicationDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Movie>().Ignore(movie => movie.Embedding);
        }
    }
}
