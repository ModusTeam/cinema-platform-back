using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Jobs;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Dtos;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Application.Orders.Queries.GetUserOrders;
using Cinema.Application.Common.Mappings.Orders;
using Cinema.Application.Orders.EventHandlers;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Application.Orders.IntegrationEvents;
using Cinema.Application.Orders.Services;
using Cinema.Application.Services;
using Cinema.Application.Seats.Commands.LockSeat;
using Cinema.Application.Tickets.Queries.GetTicketDetails;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Cinema.Infrastructure.Persistence;
using Cinema.Infrastructure.Persistence.Interceptors;
using Cinema.Orders.Application;
using Cinema.Domain.Events;
using FluentAssertions;
using MediatR;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Mapster;
using System.Data.Common;
using Xunit;

// Paid notifications intentionally call the existing overload without a cancellation token.
#pragma warning disable xUnit1051

namespace Cinema.OrdersTests;

// Only run with a disposable pgvector PostgreSQL database named cinema_orders_test_*.
public class OrderReservationPostgresTests(PostgresSchemaTemplate template) : IClassFixture<PostgresSchemaTemplate>
{
    [Fact]
    public async Task Missing_Lock_Releases_Other_Owned_Seats_Before_Reservation()
    {
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid firstSeat = Guid.NewGuid();
        Guid missingSeat = Guid.NewGuid();
        currentUser.UserId.Returns(userId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(sessionId, firstSeat, userId, Arg.Any<CancellationToken>()).Returns(true);
        locks.ValidateAndExtendLockAsync(sessionId, missingSeat, userId, Arg.Any<CancellationToken>()).Returns(false);
        IOrderReservationService reservation = Substitute.For<IOrderReservationService>();
        IOrderCheckoutOrchestrator checkout = Substitute.For<IOrderCheckoutOrchestrator>();
        CreateOrderCommandHandler handler = new(reservation, currentUser, locks, checkout,
            NullLogger<CreateOrderCommandHandler>.Instance);

        Result<Guid> result = await handler.Handle(new CreateOrderCommand
        {
            SessionId = sessionId, SeatIds = [firstSeat, missingSeat], PaymentToken = "token"
        }, TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.LockExpired");
        await locks.Received(1).UnlockSeatsAsync(sessionId, Arg.Is<IEnumerable<Guid>>(ids =>
            ids.Contains(firstSeat) && ids.Contains(missingSeat)), userId, Arg.Any<CancellationToken>());
        await reservation.DidNotReceiveWithAnyArgs().ReserveOrderAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Reservation_Exception_Releases_Owned_Lock_And_Propagates_Error()
    {
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        Guid userId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid seatId = Guid.NewGuid();
        currentUser.UserId.Returns(userId);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        locks.ValidateAndExtendLockAsync(sessionId, seatId, userId, Arg.Any<CancellationToken>()).Returns(true);
        IOrderReservationService reservation = Substitute.For<IOrderReservationService>();
        reservation.ReserveOrderAsync(userId, sessionId, Arg.Any<List<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<Result<Guid>>(new InvalidOperationException("database unavailable")));
        CreateOrderCommandHandler handler = new(reservation, currentUser, locks,
            Substitute.For<IOrderCheckoutOrchestrator>(), NullLogger<CreateOrderCommandHandler>.Instance);

        Func<Task> reserve = () => handler.Handle(new CreateOrderCommand
        {
            SessionId = sessionId, SeatIds = [seatId], PaymentToken = "token"
        }, TestContext.Current.CancellationToken);

        await reserve.Should().ThrowAsync<InvalidOperationException>();
        await locks.Received(1).UnlockSeatsAsync(sessionId, Arg.Is<IEnumerable<Guid>>(ids =>
            ids.Single() == seatId), userId, Arg.Any<CancellationToken>());
    }


    [Fact]
    public async Task Lock_Validator_Rejects_Inactive_And_Sold_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        LockSeatCommandValidator validator = new(database.Context);
        LockSeatCommand request = new(database.Session.Id.Value, database.Seat.Id.Value);
        (await validator.ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid.Should().BeTrue();

        database.Seat.SetStatus(SeatStatus.Maintenance);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await validator.ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid.Should().BeFalse();

        database.Seat.SetStatus(SeatStatus.Active);
        OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await validator.ValidateAsync(request, TestContext.Current.CancellationToken)).IsValid.Should().BeFalse();
    }

    [Fact]
    public async Task Reservation_Rejects_Duplicate_And_Foreign_Hall_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> duplicate = await database.ReserveAsync([database.Seat.Id.Value, database.Seat.Id.Value]);
        duplicate.IsFailure.Should().BeTrue();

        Hall otherHall = Hall.Create(EntityId<Hall>.New(), $"Other hall {Guid.NewGuid():N}");
        Seat foreignSeat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active,
            otherHall.Id, database.Seat.SeatTypeId);
        otherHall.ApplyLayout([foreignSeat]);
        database.Context.Halls.Add(otherHall);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Result<Guid> foreign = await database.ReserveAsync(foreignSeat.Id.Value);
        foreign.IsFailure.Should().BeTrue();
        (await database.Context.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Cancelled_Session_Cannot_Be_Locked_Or_Reserved()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        database.Session.Cancel(DateTime.UtcNow);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        LockSeatCommandValidator validator = new(database.Context);
        (await validator.ValidateAsync(new LockSeatCommand(database.Session.Id.Value, database.Seat.Id.Value),
            TestContext.Current.CancellationToken)).IsValid.Should().BeFalse();
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Session.NotAvailable");
    }

    [Fact]
    public async Task Competing_Reservations_Create_Only_One_Active_Ticket()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid>[] results = await Task.WhenAll(
            database.ReserveAsync(database.Seat.Id.Value),
            database.ReserveAsync(database.Seat.Id.Value));

        results.Count(result => result.IsSuccess).Should().Be(1);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(ticket => ticket.TicketStatus == TicketStatus.Valid,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Ticket_Details_Use_Persisted_Order_Ownership_And_Preserve_Original_Dto()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Guid ticketId = (await OrderTicketFixture.ForOrder(database.Context, order)
            .SingleAsync(TestContext.Current.CancellationToken)).Id.Value;
        new Microsoft.Extensions.DependencyInjection.ServiceCollection().AddApplication();

        HistoryCommandCounter baselineCounter = new();
        await using ApplicationDbContext baselineContext = database.NewContext(interceptor: baselineCounter);
        Ticket baselineTicket = await baselineContext.Tickets.AsNoTracking()
            .Include(ticket => ticket.Session!).ThenInclude(session => session.Movie)
            .Include(ticket => ticket.Session!).ThenInclude(session => session.Hall)
            .Include(ticket => ticket.Seat!).ThenInclude(seat => seat.SeatType)
            .SingleAsync(ticket => ticket.Id == new EntityId<Ticket>(ticketId), TestContext.Current.CancellationToken);
        await using ApplicationDbContext ownershipContext = database.NewContext();
        (await ownershipContext.Orders.SingleAsync(o => o.Id == baselineTicket.OrderId, TestContext.Current.CancellationToken))
            .UserId.Should().Be(database.UserId);
        TicketDto baselineDto = baselineTicket.Adapt<TicketDto>();
        baselineCounter.Commands.Should().ContainSingle();

        HistoryCommandCounter counter = new();
        await using ApplicationDbContext queryContext = database.NewContext(interceptor: counter);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        GetTicketDetailsQueryHandler handler = new(queryContext, currentUser);

        Result<TicketDto> owner = await handler.Handle(new GetTicketDetailsQuery(ticketId),
            TestContext.Current.CancellationToken);
        owner.IsSuccess.Should().BeTrue();
        owner.Value.Should().Be(baselineDto);
        counter.Commands.Should().ContainSingle();
        counter.Commands[0].Should().Contain("tickets").And.Contain("orders").And.Contain("user_id");

        counter.Commands.Clear();
        currentUser.UserId.Returns(Guid.NewGuid());
        Result<TicketDto> stranger = await handler.Handle(new GetTicketDetailsQuery(ticketId),
            TestContext.Current.CancellationToken);
        stranger.IsFailure.Should().BeTrue();
        stranger.Error.Code.Should().Be("Ticket.NotFound");
        counter.Commands.Should().ContainSingle();

        counter.Commands.Clear();
        currentUser.UserId.Returns(database.UserId);
        Result<TicketDto> missing = await handler.Handle(new GetTicketDetailsQuery(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        missing.Error.Code.Should().Be("Ticket.NotFound");
        counter.Commands.Should().ContainSingle();

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        counter.Commands.Clear();
        Result<TicketDto> hidden = await handler.Handle(new GetTicketDetailsQuery(ticketId),
            TestContext.Current.CancellationToken);
        hidden.IsSuccess.Should().BeTrue();
        counter.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task History_Batches_Tickets_And_Matches_Original_Projection()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat secondSeat = await database.AddSeatAsync();
        Order first = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat, secondSeat],
            new() { [database.Seat.Id] = 50m, [secondSeat.Id] = 60m });
        Session foreignSession = Session.Create(EntityId<Session>.New(),
            database.Session.StartTime.AddDays(1), database.Session.StartTime.AddDays(1).AddHours(2),
            database.Session.MovieId, database.Session.HallId, database.Session.PricingId);
        Order second = Order.New(EntityId<Order>.New(), 20m, database.UserId, foreignSession.Id);
        User foreignUser = new() { Id = Guid.NewGuid(), UserName = $"foreign-{Guid.NewGuid():N}" };
        Order foreign = OrderTicketFixture.CreateAndTrack(database.Context, foreignUser.Id, foreignSession, [secondSeat],
            new() { [secondSeat.Id] = 70m });
        database.Context.Users.Add(foreignUser);
        database.Context.Sessions.Add(foreignSession);
        database.Context.Orders.Add(second);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        TypeAdapterConfig original = new();
        new TicketMappingConfig().Register(original);
        original.NewConfig<Order, OrderDto>()
            .Map(dest => dest.Id, src => src.Id.Value)
            .Map(dest => dest.TotalAmount, src => src.TotalAmount)
            .Map(dest => dest.CreatedAt, src => src.BookingDate)
            .Map(dest => dest.Status, src => src.Status.ToString())
            .Ignore(dest => dest.Tickets);
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddApplication();
        services.AddOrdersApplication();
        await using ApplicationDbContext baselineContext = database.NewContext();
        List<OrderDto> baseline = await baselineContext.Orders.AsNoTracking()
            .Where(order => order.UserId == database.UserId)
            .OrderByDescending(order => order.BookingDate)
            .ProjectToType<OrderDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        foreach (OrderDto dto in baseline)
            dto.Tickets = await baselineContext.Tickets.AsNoTracking()
                .Where(ticket => ticket.OrderId == new EntityId<Order>(dto.Id))
                .ProjectToType<TicketDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        HistoryCommandCounter counter = new();
        await using ApplicationDbContext queryContext = database.NewContext(interceptor: counter);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);

        Result<OrderHistoryVm> mine = await new GetMyOrdersQueryHandler(queryContext, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        mine.IsSuccess.Should().BeTrue();
        mine.Value.ActiveOrders.Should().HaveCount(2);
        mine.Value.ActiveOrders.Select(order => order.Id).Should().Equal(first.Id.Value, second.Id.Value);
        mine.Value.ActiveOrders.Single(order => order.Id == first.Id.Value).Tickets.Should().HaveCount(2);
        mine.Value.ActiveOrders.Single(order => order.Id == second.Id.Value).Tickets.Should().BeEmpty();
        counter.Commands.Should().HaveCount(3);
        counter.Commands.Last().Should().Contain("tickets").And.Contain("order_id");

        counter.Commands.Clear();
        Result<List<OrderDto>> admin = await new GetUserOrdersQueryHandler(queryContext)
            .Handle(new GetUserOrdersQuery(database.UserId), TestContext.Current.CancellationToken);
        counter.Commands.Should().HaveCount(2);
        admin.Value.Should().BeEquivalentTo(baseline);
        admin.Value.Select(order => order.Id).Should().Equal(baseline.Select(order => order.Id));
        admin.Value.Select(order => order.Id).Should().NotContain(foreign.Id.Value);

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext filtered = database.NewContext();
        List<OrderDto> filteredBaseline = await filtered.Orders.AsNoTracking()
            .Where(order => order.UserId == database.UserId)
            .ProjectToType<OrderDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        foreach (OrderDto dto in filteredBaseline)
            dto.Tickets = await filtered.Tickets.AsNoTracking()
                .Where(ticket => ticket.OrderId == new EntityId<Order>(dto.Id))
                .ProjectToType<TicketDto>(original).ToListAsync(TestContext.Current.CancellationToken);
        Result<List<OrderDto>> filteredHistory = await new GetUserOrdersQueryHandler(filtered)
            .Handle(new GetUserOrdersQuery(database.UserId), TestContext.Current.CancellationToken);
        filteredHistory.Value.Should().HaveCount(2);
        filteredHistory.Value.Single(order => order.Id == first.Id.Value).Tickets.Should().HaveCount(2);
        filteredHistory.Value.Single(order => order.Id == second.Id.Value).Tickets.Should().BeEmpty();
    }

    [Fact]
    public async Task Required_Session_Filter_Drops_Include_But_History_Preserves_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session,
            [database.Seat], new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext read = database.NewContext();
        (await read.Orders.AsNoTracking().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await read.Orders.AsNoTracking().Include(item => item.Session)
            .ToListAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        Result<OrderHistoryVm> history = await new GetMyOrdersQueryHandler(read, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        history.Value.ActiveOrders.Concat(history.Value.PastOrders)
            .Select(item => item.Id).Should().Contain(order.Id.Value);
    }

    private sealed class HistoryCommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Paid_Event_Queries_Persisted_Tickets_During_SaveChanges()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        IPublisher publisher = Substitute.For<IPublisher>();
        OrderPaidEventHandler? handler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderPaidEvent> notification
                ? handler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        await using ApplicationDbContext context = database.NewContext(publisher);
        handler = new(context, notifier, locks, NullLogger<OrderPaidEventHandler>.Instance);
        Order order = await context.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken);
        context.ChangeTracker.Entries<Ticket>().Should().BeEmpty();

        order.MarkAsPaid("paid-test");
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await locks.Received(1).UnlockSeatAsync(database.Session.Id.Value, database.Seat.Id.Value,
            database.UserId, TestContext.Current.CancellationToken);
        await locks.Received(1).UnlockSeatAsync(database.Session.Id.Value, second.Id.Value,
            database.UserId, TestContext.Current.CancellationToken);
        await notifier.Received(1).NotifyOrderCompleted(database.UserId, reserved.Value);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Paid_Event_Includes_Added_Ticket_Before_Insert()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        IPublisher publisher = Substitute.For<IPublisher>();
        OrderPaidEventHandler? handler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderPaidEvent> notification
                ? handler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        await using ApplicationDbContext context = database.NewContext(publisher);
        handler = new(context, notifier, locks, NullLogger<OrderPaidEventHandler>.Instance);
        Order order = OrderTicketFixture.CreateAndTrack(context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        order.MarkAsPaid("paid-on-insert");

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await locks.Received(1).UnlockSeatAsync(database.Session.Id.Value, database.Seat.Id.Value,
            database.UserId, TestContext.Current.CancellationToken);
        await notifier.Received(1).NotifyOrderCompleted(database.UserId, order.Id.Value);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.SingleAsync(t => t.OrderId == order.Id, TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Cancellation_Loads_Tracked_Tickets_And_Dispatches_Seat_Side_Effects()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        reserved.IsSuccess.Should().BeTrue();

        ISeatLockingService locks = Substitute.For<ISeatLockingService>();
        ITicketNotifier notifier = Substitute.For<ITicketNotifier>();
        IPublisher publisher = Substitute.For<IPublisher>();
        OrderCancelledEventHandler? eventHandler = null;
        publisher.Publish(Arg.Any<INotification>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<INotification>(0) is DomainEventNotification<OrderCancelledDomainEvent> notification
                ? eventHandler!.Handle(notification, call.ArgAt<CancellationToken>(1))
                : Task.CompletedTask);
        await using ApplicationDbContext cancellation = database.NewContext(publisher);
        eventHandler = new(cancellation, locks, notifier,
            NullLogger<OrderCancelledEventHandler>.Instance);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();

        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment, Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        Order trackedOrder = cancellation.ChangeTracker.Entries<Order>().Single().Entity;
        List<Ticket> refundedTickets = await OrderTicketFixture.ForOrder(cancellation, trackedOrder)
            .ToListAsync(TestContext.Current.CancellationToken);
        refundedTickets.Should().HaveCount(2);
        refundedTickets.Should().OnlyContain(t => t.OrderId == trackedOrder.Id && t.TicketStatus == TicketStatus.Refunded);
        await locks.Received(1).UnlockSeatsAsync(database.Session.Id.Value,
            Arg.Is<IEnumerable<Guid>>(ids => ids.ToHashSet().SetEquals(new[] { database.Seat.Id.Value, second.Id.Value })),
            database.UserId, Arg.Any<CancellationToken>(), false);
        await notifier.Received(1).NotifySeatUnlockedAsync(database.Session.Id.Value, database.Seat.Id.Value,
            Arg.Any<CancellationToken>());
        await notifier.Received(1).NotifySeatUnlockedAsync(database.Session.Id.Value, second.Id.Value,
            Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Tickets.Where(t => t.OrderId == new EntityId<Order>(reserved.Value))
            .ToListAsync(TestContext.Current.CancellationToken)).Should().OnlyContain(t => t.TicketStatus == TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancellation_Refunds_Ticket_In_Inactive_Hall()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext cancellation = database.NewContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsInRole("Admin").Returns(true);

        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, Substitute.For<IPaymentService>(), Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        cancellation.ChangeTracker.Entries<Ticket>().Should().ContainSingle();
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.IgnoreQueryFilters().SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Refunded);
    }

    [Fact]
    public async Task Paid_Cancellation_Refunds_Payment_And_Points_Once_And_Persists_Tickets()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            order.ApplyLoyaltyDiscount(10);
            order.MarkAsPaid("payment-1");
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("refund-1"));
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.RefundPointsAsync(database.UserId, 10, reserved.Value, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));
        await using (ApplicationDbContext cancellation = database.NewContext())
        {
            Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment, loyalty)
                .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);
            result.IsSuccess.Should().BeTrue();
        }
        await using (ApplicationDbContext repeated = database.NewContext())
        {
            Result result = await new CancelOrderCommandHandler(repeated, currentUser, payment, loyalty)
                .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);
            result.Error.Code.Should().Be("Order.AlreadyCancelled");
        }

        await payment.Received(1).RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>());
        await loyalty.Received(1).RefundPointsAsync(database.UserId, 10, reserved.Value,
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Tickets.Where(ticket => ticket.OrderId == new EntityId<Order>(reserved.Value))
            .ToListAsync(TestContext.Current.CancellationToken)).Should()
            .OnlyContain(ticket => ticket.TicketStatus == TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancellation_Without_Tickets_Does_Not_Refund_Or_Change_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = Order.New(EntityId<Order>.New(), 50m, database.UserId, database.Session.Id);
        order.MarkAsPaid("payment-1");
        database.Context.Orders.Add(order);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();

        await using ApplicationDbContext cancellation = database.NewContext();
        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment,
            Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(order.Id.Value),
            TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == order.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Inactive_Hall_Purchased_Ticket_Remains_Visible_Only_To_Owner()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            order.MarkAsPaid("payment-1");
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        Guid ticketId = (await database.Context.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Id.Value;
        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext read = database.NewContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        Result<TicketDto> owned = await new GetTicketDetailsQueryHandler(read, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);
        owned.IsSuccess.Should().BeTrue();
        owned.Value.Id.Should().Be(ticketId);
        Result<Cinema.Application.Orders.Queries.GetMyOrders.OrderHistoryVm> history = await new GetMyOrdersQueryHandler(read, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        history.IsSuccess.Should().BeTrue();
        history.Value.ActiveOrders.Concat(history.Value.PastOrders).Single().Tickets.Should().ContainSingle();

        currentUser.UserId.Returns(Guid.NewGuid());
        Result<TicketDto> foreign = await new GetTicketDetailsQueryHandler(read, currentUser)
            .Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);
        foreign.Error.Code.Should().Be("Ticket.NotFound");
        (await read.Seats.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await read.Sessions.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Paid_Cancellation_Rolls_Back_Consumed_Gold_Upgrade()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            Ticket ticket = await paidContext.Tickets.SingleAsync(t => t.OrderId == order.Id,
                TestContext.Current.CancellationToken);
            order.ApplyGoldDiscount(20m);
            ticket.ApplyGoldUpgrade(30m);
            order.MarkAsPaid("payment-1");
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("refund-1"));
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>())
            .Returns((true, string.Empty));

        await using ApplicationDbContext cancellation = database.NewContext();
        Result result = await new CancelOrderCommandHandler(cancellation, currentUser, payment, loyalty)
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await loyalty.Received(1).RollbackGoldUpgradeAsync(database.UserId, reserved.Value,
            Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancellation_At_Ten_Minute_Cutoff_Leaves_Order_And_Ticket_Valid()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        DateTime cutoffStart = DateTime.UtcNow.AddMinutes(10);
        await database.Context.Sessions.Where(session => session.Id == database.Session.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(session => session.StartTime, cutoffStart),
                TestContext.Current.CancellationToken);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);

        await using ApplicationDbContext cancellation = database.NewContext();
        Result result = await new CancelOrderCommandHandler(cancellation, currentUser,
            Substitute.For<IPaymentService>(), Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(reserved.Value), TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Order.TooLate");
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Pending);
        (await verify.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Database_Save_Failure_After_Payment_Refund_Leaves_Paid_Order_For_Reconciliation()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext paidContext = database.NewContext())
        {
            Order order = await paidContext.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
                TestContext.Current.CancellationToken);
            order.MarkAsPaid("payment-1");
            await paidContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(database.UserId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Success("refund-1"));
        await using ApplicationDbContext cancellation = database.NewContext(interceptor: new FailOrderUpdateInterceptor());

        Func<Task> cancel = async () => await new CancelOrderCommandHandler(cancellation, currentUser, payment,
            Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(reserved.Value),
            TestContext.Current.CancellationToken);

        DbUpdateException failure = (await cancel.Should().ThrowAsync<DbUpdateException>()).Which;
        failure.InnerException.Should().BeOfType<InvalidOperationException>();
        await payment.Received(1).RefundPaymentAsync("payment-1", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Paid);
        (await verify.Tickets.SingleAsync(t => t.OrderId == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Valid);
    }

    private sealed class FailOrderUpdateInterceptor : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("UPDATE orders", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Simulated cancellation save failure.");
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Reservation_Persists_Multiple_Tickets_With_One_Tracked_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();

        Result<Guid> reserved = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);

        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        order.Status.Should().Be(OrderStatus.Pending);
        order.TotalAmount.Should().Be(100m);
        order.PaidAmount.Should().Be(100m);
        List<Ticket> tickets = await OrderTicketFixture.ForOrder(verify, order)
            .ToListAsync(TestContext.Current.CancellationToken);
        tickets.Should().HaveCount(2);
        tickets.Select(t => t.SeatId).Should().BeEquivalentTo([database.Seat.Id, second.Id]);
        tickets.Should().OnlyContain(t => t.OrderId == order.Id &&
            t.SessionId == database.Session.Id && t.PriceSnapshot == 50m && t.TicketStatus == TicketStatus.Valid);
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Gold_Checkout_Persists_Ticket_And_Order_Discount_Together()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        Order persisted = await verify.Orders
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(30m);
        persisted.PaidAmount.Should().Be(30m);
        Ticket persistedTicket = await OrderTicketFixture.ForOrder(verify, persisted)
            .SingleAsync(TestContext.Current.CancellationToken);
        persistedTicket.PriceSnapshot.Should().Be(30m);
        persistedTicket.IsGoldUpgraded.Should().BeTrue();
        persistedTicket.OrderId.Should().Be(persisted.Id);
    }

    [Fact]
    public async Task Checkout_Publishes_Persisted_Ticket_Count_For_Multiple_Seats()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        Order persisted = await verify.Orders
            .SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        persisted.TotalAmount.Should().Be(80m);
        persisted.PaidAmount.Should().Be(80m);
        List<Ticket> persistedTickets = await OrderTicketFixture.ForOrder(verify, persisted)
            .ToListAsync(TestContext.Current.CancellationToken);
        persistedTickets.Should().ContainSingle(t => t.IsGoldUpgraded && t.PriceSnapshot == 30m);
        persistedTickets.Should().ContainSingle(t => !t.IsGoldUpgraded && t.PriceSnapshot == 50m);
    }

    [Fact]
    public async Task Checkout_Confirmation_Count_Respects_Ticket_Query_Filter()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);

        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order order = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value), TestContext.Current.CancellationToken);
        order.Status.Should().Be(OrderStatus.Pending);
        (await OrderTicketFixture.ForOrder(verify, order).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
        order.TotalAmount.Should().Be(50m);
        (await database.ReserveAsync(database.Seat.Id.Value)).Error.Code.Should().Be("Order.SeatsSold");
        (await verify.Orders.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Declined_Payment_Releases_Tickets_And_Allows_Resale()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        reserved.IsSuccess.Should().BeTrue();
        await using ApplicationDbContext checkoutContext = database.NewContext();
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(50m, "UAH", "test_fail_token", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure("declined"));
        OrderCheckoutOrchestrator checkout = new(checkoutContext, Substitute.For<ILoyaltyService>(), payment,
            Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(),
            Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);

        Result<Guid> result = await checkout.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, false, false, "test_fail_token",
            TestContext.Current.CancellationToken);

        result.Error.Code.Should().Be("Payment.Failed");
        checkoutContext.ChangeTracker.Entries<Ticket>().Should().ContainSingle()
            .Which.State.Should().Be(EntityState.Unchanged);
        await using ApplicationDbContext verify = database.NewContext();
        Order failed = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reserved.Value),
            TestContext.Current.CancellationToken);
        failed.Status.Should().Be(OrderStatus.Failed);
        (await OrderTicketFixture.ForOrder(verify, failed).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Refunded);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Expiration_Releases_All_Unpaid_Tickets_Once_And_Preserves_Paid_Orders()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Seat second = await database.AddSeatAsync();
        Result<Guid> expiredId = await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value]);
        expiredId.IsSuccess.Should().BeTrue();
        Seat paidSeat = Seat.New(EntityId<Seat>.New(), "A", 3, 1, 3, SeatStatus.Active,
            database.Seat.HallId, database.Seat.SeatTypeId);
        database.Context.Seats.Add(paidSeat);
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Result<Guid> paidId = await database.ReserveAsync(paidSeat.Id.Value);
        paidId.IsSuccess.Should().BeTrue();
        await using (ApplicationDbContext seed = database.NewContext())
        {
            Order expired = await seed.Orders.SingleAsync(o => o.Id == new EntityId<Order>(expiredId.Value),
                TestContext.Current.CancellationToken);
            seed.Entry(expired).Property(o => o.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            Order paid = await seed.Orders.SingleAsync(o => o.Id == new EntityId<Order>(paidId.Value),
                TestContext.Current.CancellationToken);
            paid.MarkAsPaid("already-paid");
            seed.Entry(paid).Property(o => o.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext jobContext = database.NewContext();
        CancelExpiredOrdersJob job = new(jobContext, NullLogger<CancelExpiredOrdersJob>.Instance);

        await job.Process(TestContext.Current.CancellationToken);
        await job.Process(TestContext.Current.CancellationToken);

        await using ApplicationDbContext verify = database.NewContext();
        Order cancelled = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(expiredId.Value),
            TestContext.Current.CancellationToken);
        cancelled.Status.Should().Be(OrderStatus.Cancelled);
        (await OrderTicketFixture.ForOrder(verify, cancelled).ToListAsync(TestContext.Current.CancellationToken))
            .Should().HaveCount(2).And.OnlyContain(t => t.TicketStatus == TicketStatus.Refunded);
        Order persistedPaid = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(paidId.Value),
            TestContext.Current.CancellationToken);
        persistedPaid.Status.Should().Be(OrderStatus.Paid);
        (await OrderTicketFixture.ForOrder(verify, persistedPaid).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
        (await database.ReserveAsync([database.Seat.Id.Value, second.Id.Value])).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_And_Inactive_Seats_And_Missing_Session_Do_Not_Create_Orders()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        await using NpgsqlConnection connection = new(database.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (NpgsqlCommand command = new("SELECT id FROM seats WHERE id = @id FOR UPDATE", connection, transaction))
        {
            command.Parameters.AddWithValue("id", database.Seat.Id.Value);
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken); // Lock acquired before reservation starts.
        }

        Result<Guid> conflict = await database.ReserveAsync(database.Seat.Id.Value);
        conflict.Error.Code.Should().Be("Order.SeatsUnavailable");
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Checkout_Failure_After_Reservation_Leaves_Committed_Pending_Order()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
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
        Order persisted = await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(reservedOrderId),
            TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Pending);
        (await OrderTicketFixture.ForOrder(verify, persisted).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Valid);
    }

    [Fact]
    public async Task Saving_Order_Tracks_Tickets_And_Database_Cascades_Their_Deletion()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using ApplicationDbContext verify = database.NewContext();
        Ticket ticket = await verify.Tickets.AsNoTracking()
            .Include(t => t.Session).Include(t => t.Seat)
            .SingleAsync(TestContext.Current.CancellationToken);
        ticket.OrderId.Should().Be(order.Id);
        ticket.Session!.Id.Should().Be(database.Session.Id);
        ticket.Seat!.Id.Should().Be(database.Seat.Id);

        await using (NpgsqlConnection connection = new(database.ConnectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand constraint = new(
                "SELECT pg_get_constraintdef(oid) FROM pg_constraint WHERE conname = 'fk_tickets_orders_order_id'",
                connection);
            (await constraint.ExecuteScalarAsync(TestContext.Current.CancellationToken))
                .Should().Be("FOREIGN KEY (order_id) REFERENCES orders(id) ON DELETE CASCADE");
        }

        await using (ApplicationDbContext invalid = database.NewContext())
        {
            invalid.Tickets.Add(Ticket.New(EntityId<Ticket>.New(), 50m, TicketStatus.Refunded,
                EntityId<Order>.New(), database.Session.Id, database.Seat.Id));
            Func<Task> save = () => invalid.SaveChangesAsync(TestContext.Current.CancellationToken);
            (await save.Should().ThrowAsync<DbUpdateException>()).Which.InnerException
                .Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        }

        await verify.Orders.Where(o => o.Id == order.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext afterDelete = database.NewContext();
        (await afterDelete.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Theory]
    [InlineData(TicketStatus.Valid)]
    [InlineData(TicketStatus.Used)]
    public async Task Partial_Index_Rejects_Valid_Or_Used_Duplicate_But_Allows_Refunded_Tickets(TicketStatus existingStatus)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order first = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Ticket original = await OrderTicketFixture.ForOrder(database.Context, first)
            .SingleAsync(TestContext.Current.CancellationToken);
        if (existingStatus == TicketStatus.Used)
        {
            original.MarkAsUsed();
            await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (ApplicationDbContext duplicateContext = database.NewContext())
        {
            Order duplicate = OrderTicketFixture.CreateAndTrack(duplicateContext, database.UserId, database.Session, [database.Seat],
                new() { [database.Seat.Id] = 50m });
            Func<Task> saveDuplicate = () => duplicateContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            DbUpdateException failure = (await saveDuplicate.Should().ThrowAsync<DbUpdateException>()).Which;
            failure.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        }

        // Refunded tickets are outside the partial index, including multiple tickets for one seat.
        Order refundedOne = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        OrderTicketFixture.TrackedForOrder(database.Context, refundedOne).Single().MarkAsRefunded();
        Order refundedTwo = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        OrderTicketFixture.TrackedForOrder(database.Context, refundedTwo).Single().MarkAsRefunded();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(3);
        (await verify.Tickets.CountAsync(t => t.TicketStatus == TicketStatus.Refunded,
            TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Ticket_Query_Filter_Hides_Tickets_In_Inactive_Halls()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Order order = OrderTicketFixture.CreateAndTrack(database.Context, database.UserId, database.Session, [database.Seat],
            new() { [database.Seat.Id] = 50m });
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        (await database.Context.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);

        Hall hall = await database.Context.Halls.SingleAsync(TestContext.Current.CancellationToken);
        hall.Deactivate();
        await database.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        (await verify.Tickets.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await verify.Tickets.IgnoreQueryFilters().CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Abandoned_Gold_Journal_Is_Recovered_From_A_Fresh_Context(bool useConfirmed, bool paymentStarted)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        await using (ApplicationDbContext interrupted = database.NewContext())
        {
            Order order = await interrupted.Orders.SingleAsync(TestContext.Current.CancellationToken);
            Ticket ticket = await interrupted.Tickets.SingleAsync(TestContext.Current.CancellationToken);
            order.BeginGoldCheckout(ticket.Id.Value, 50m);
            await interrupted.SaveChangesAsync(TestContext.Current.CancellationToken);
            if (useConfirmed)
            {
                order.ConfirmGoldUse();
                order.ApplyGoldDiscount(20m);
                ticket.ApplyGoldUpgrade(30m);
                await interrupted.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            if (paymentStarted)
            {
                order.BeginGoldPayment();
                await interrupted.SaveChangesAsync(TestContext.Current.CancellationToken);
            }
            // No request cleanup runs: only the committed journal survives.
        }
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        loyalty.RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        await using (ApplicationDbContext recovery = database.NewContext())
        {
            OrderCheckoutOrchestrator worker = new(recovery, loyalty, Substitute.For<IPaymentService>(),
                Substitute.For<ISeatLockingService>(), Substitute.For<IGoldUpgradePricingService>(),
                Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
            await worker.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
            await worker.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
        }
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        persisted.Status.Should().Be(paymentStarted ? OrderStatus.Pending : OrderStatus.Failed);
        persisted.GoldPaymentOutcomeUnknown.Should().Be(paymentStarted);
        persisted.TotalAmount.Should().Be(50m);
        persisted.PaidAmount.Should().Be(50m);
        Ticket restored = await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken);
        restored.PriceSnapshot.Should().Be(50m);
        restored.IsGoldUpgraded.Should().BeFalse();
        restored.TicketStatus.Should().Be(paymentStarted ? TicketStatus.Valid : TicketStatus.Refunded);
        (await database.ReserveAsync(database.Seat.Id.Value)).IsSuccess.Should().Be(!paymentStarted);
        await loyalty.Received(useConfirmed ? 0 : 1).UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>());
        await loyalty.Received(1).RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Recovery_And_Repeated_Checkout_Cannot_Compensate_An_Active_Checkout()
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        TaskCompletionSource enteredUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            enteredUse.SetResult();
            await resumeUse.Task.WaitAsync(TestContext.Current.CancellationToken);
            return (true, "");
        });
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("paid"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, database.Seat.Id.Value, 50m, 30m, null)));
        await using ApplicationDbContext activeContext = database.NewContext();
        OrderCheckoutOrchestrator active = new(activeContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
            gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
        Task<Result<Guid>> checkout = active.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, true, false, "token", TestContext.Current.CancellationToken);
        await enteredUse.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            await using ApplicationDbContext recoveryContext = database.NewContext();
            OrderCheckoutOrchestrator recovery = new(recoveryContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
                gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
            await recovery.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
            Result<Guid> duplicate = await recovery.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
                [database.Seat.Id.Value], reserved.Value, true, false, "token", TestContext.Current.CancellationToken);
            duplicate.Error.Code.Should().Be("Order.CheckoutInProgress");
            await loyalty.DidNotReceiveWithAnyArgs().RollbackGoldUpgradeAsync(default, default, TestContext.Current.CancellationToken);
        }
        finally { resumeUse.TrySetResult(); }
        (await checkout).IsSuccess.Should().BeTrue();
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Paid);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Completed);
        persisted.TotalAmount.Should().Be(30m);
    }

    [Theory]
    [InlineData("use")]
    [InlineData("discount")]
    public async Task Lost_Postgres_Session_Cannot_Resume_Checkout_After_Recovery_Releases_Gold(string stage)
    {
        await using ReservationDatabase database = await ReservationDatabase.CreateAsync(template);
        Result<Guid> reserved = await database.ReserveAsync(database.Seat.Id.Value);
        TaskCompletionSource enteredUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource resumeUse = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        ILoyaltyService loyalty = Substitute.For<ILoyaltyService>();
        loyalty.UseGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (Interlocked.Increment(ref calls) == 1 && stage == "use")
            {
                enteredUse.SetResult();
                await resumeUse.Task.WaitAsync(TestContext.Current.CancellationToken);
            }
            return (true, "");
        });
        loyalty.CalculateDiscountAsync(database.UserId, 30m, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            enteredUse.SetResult();
            await resumeUse.Task.WaitAsync(TestContext.Current.CancellationToken);
            return (false, 0, 30m);
        });
        loyalty.RollbackGoldUpgradeAsync(database.UserId, reserved.Value, Arg.Any<CancellationToken>()).Returns((true, ""));
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.ProcessPaymentAsync(30m, "UAH", "token", Arg.Any<CancellationToken>()).Returns(PaymentResult.Success("must-not-charge"));
        IGoldUpgradePricingService gold = Substitute.For<IGoldUpgradePricingService>();
        gold.CalculateAsync(Arg.Any<Session>(), Arg.Any<IReadOnlyCollection<GoldUpgradeTicketPrice>>(), Arg.Any<CancellationToken>())
            .Returns(Result.Success(new GoldUpgradePricingQuote(true, 30m, 20m, database.Seat.Id.Value, 50m, 30m, null)));
        await using ApplicationDbContext activeContext = database.NewContext(retries: true);
        OrderCheckoutOrchestrator active = new(activeContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
            gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
        Task<Result<Guid>> checkout = active.ProcessCheckoutAsync(database.UserId, database.Session.Id.Value,
            [database.Seat.Id.Value], reserved.Value, true, stage == "discount", "token", TestContext.Current.CancellationToken);
        await enteredUse.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            int backend = ((NpgsqlConnection)activeContext.Database.GetDbConnection()).ProcessID;
            await using NpgsqlConnection admin = new(database.ConnectionString);
            await admin.OpenAsync(TestContext.Current.CancellationToken);
            await using NpgsqlCommand terminate = new("SELECT pg_terminate_backend(@backend)", admin);
            terminate.Parameters.AddWithValue("backend", backend);
            await terminate.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            await using ApplicationDbContext recoveryContext = database.NewContext(retries: true);
            OrderCheckoutOrchestrator recovery = new(recoveryContext, loyalty, payment, Substitute.For<ISeatLockingService>(),
                gold, Substitute.For<IPublishEndpoint>(), NullLogger<OrderCheckoutOrchestrator>.Instance);
            await recovery.RecoverGoldCheckoutsAsync(TestContext.Current.CancellationToken);
        }
        finally { resumeUse.TrySetResult(); }
        try { (await checkout).IsSuccess.Should().BeFalse(); }
        catch (Exception exception) when (exception is OperationCanceledException or NpgsqlException or InvalidOperationException or DbUpdateException { InnerException: NpgsqlException }) { }
        await payment.DidNotReceiveWithAnyArgs().ProcessPaymentAsync(default, default!, default!, TestContext.Current.CancellationToken);
        await using ApplicationDbContext verify = database.NewContext();
        Order persisted = await verify.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Failed);
        persisted.GoldCheckoutState.Should().Be(GoldCheckoutState.Compensated);
        persisted.TotalAmount.Should().Be(50m);
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

        public static async Task<ReservationDatabase> CreateAsync(PostgresSchemaTemplate template)
        {
            (string connectionString, string adminConnectionString, string databaseName) =
                await template.CreateDatabaseAsync();
            ReservationDatabase database = new(connectionString, adminConnectionString, databaseName);
            try
            {
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

        public ApplicationDbContext NewContext(IPublisher? publisher = null, DbCommandInterceptor? interceptor = null, bool retries = false)
        {
            DbContextOptionsBuilder<ApplicationDbContext> builder = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(ConnectionString, x => { x.UseVector(); if (retries) x.EnableRetryOnFailure(3); }).UseSnakeCaseNamingConvention();
            if (publisher is not null)
                builder.AddInterceptors(new DispatchDomainEventsInterceptor(publisher));
            if (interceptor is not null)
                builder.AddInterceptors(interceptor);
            DbContextOptions<ApplicationDbContext> options = builder.Options;
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
