using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Application.Jobs;
using Cinema.Application.Tickets.Queries.GetTicketDetails;
using Cinema.Catalog.Domain.Entities;
using Cinema.Catalog.Domain.Enums;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using Cinema.Infrastructure.Persistence;
using Cinema.Orders.Application;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class OrderHandlersTests
{
    [Theory]
    [InlineData(false, 20, true, null)]
    [InlineData(false, 5, false, "Order.TooLate")]
    [InlineData(true, 5, true, null)]
    public async Task Cancel_Uses_Ticket_Session_Cutoff_In_Fresh_Context(
        bool isAdmin, int minutesUntilStart, bool succeeds, string? errorCode)
    {
        string databaseName = Guid.NewGuid().ToString();
        (Guid orderId, Guid userId, _) = await SeedCancellationOrderAsync(databaseName, minutesUntilStart);
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        currentUser.IsInRole("Admin").Returns(isAdmin);

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser,
            Substitute.For<IPaymentService>()).Handle(new CancelOrderCommand(orderId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().Be(succeeds);
        if (!succeeds)
            result.Error.Code.Should().Be(errorCode);
        await using ApplicationDbContext verify = CreateContext(databaseName);
        (await verify.Orders.SingleAsync(o => o.Id == new EntityId<Order>(orderId), TestContext.Current.CancellationToken)).Status
            .Should().Be(succeeds ? OrderStatus.Cancelled : OrderStatus.Pending);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus
            .Should().Be(succeeds ? TicketStatus.Refunded : TicketStatus.Valid);
    }

    [Fact]
    public async Task Cancel_Paid_Order_Refunds_Before_Marking_Tickets()
    {
        string databaseName = Guid.NewGuid().ToString();
        (Guid orderId, Guid userId, _) = await SeedCancellationOrderAsync(databaseName, 20, paid: true);
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("transaction-1", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                context.ChangeTracker.Entries<Order>().Single().Entity.Status.Should().Be(OrderStatus.Paid);
                context.ChangeTracker.Entries<Ticket>().Single().Entity.TicketStatus.Should().Be(TicketStatus.Valid);
                return PaymentResult.Success("refund-1");
            });

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment)
            .Handle(new CancelOrderCommand(orderId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync("transaction-1", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = CreateContext(databaseName);
        (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancel_Used_Ticket_Preserves_Domain_Exception()
    {
        string databaseName = Guid.NewGuid().ToString();
        (Guid orderId, Guid userId, _) = await SeedCancellationOrderAsync(databaseName, 20, used: true);
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);

        Func<Task> cancel = async () => await new CancelOrderCommandHandler(context, currentUser,
            Substitute.For<IPaymentService>()).Handle(new CancelOrderCommand(orderId), TestContext.Current.CancellationToken);

        await cancel.Should().ThrowAsync<DomainException>();
        await using ApplicationDbContext verify = CreateContext(databaseName);
        (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Pending);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Used);
    }

    [Fact]
    public async Task Cancel_Returns_NotFound_Without_Calling_Payment()
    {
        await using ApplicationDbContext context = CreateContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        IPaymentService payment = Substitute.For<IPaymentService>();
        CancelOrderCommandHandler handler = new(context, currentUser, payment);

        Cinema.Domain.Shared.Result result = await handler.Handle(new CancelOrderCommand(Guid.NewGuid()), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Order.NotFound");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Cancel_Rejects_Another_Users_Order()
    {
        await using ApplicationDbContext context = CreateContext();
        Order order = Order.New(EntityId<Order>.New(), 25m, Guid.NewGuid(), EntityId<Session>.New());
        context.Orders.Add(order);
        await context.SaveChangesAsync(CancellationToken.None);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(Guid.NewGuid());
        currentUser.IsInRole("Admin").Returns(false);
        IPaymentService payment = Substitute.For<IPaymentService>();

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment)
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.Error.Code.Should().Be("Order.AccessDenied");
        order.Status.Should().Be(OrderStatus.Pending);
    }

    [Theory]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Failed)]
    public async Task Cancel_Rejects_Already_Cancelled_Or_Failed_Order(OrderStatus status)
    {
        await using ApplicationDbContext context = CreateContext();
        Guid userId = Guid.NewGuid();
        Order order = Order.New(EntityId<Order>.New(), 25m, userId, EntityId<Session>.New());
        if (status == OrderStatus.Cancelled) order.MarkAsCancelled();
        else order.MarkAsFailed();
        context.Orders.Add(order);
        await context.SaveChangesAsync(CancellationToken.None);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        IPaymentService payment = Substitute.For<IPaymentService>();

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment)
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.Error.Code.Should().Be("Order.AlreadyCancelled");
    }

    [Fact]
    public async Task Cancel_Does_Not_Change_Paid_Order_When_Refund_Fails()
    {
        await using ApplicationDbContext context = CreateContext();
        Order order = Order.New(EntityId<Order>.New(), 25m, Guid.NewGuid(), EntityId<Session>.New());
        order.MarkAsPaid("transaction-1");
        context.Orders.Add(order);
        await context.SaveChangesAsync(CancellationToken.None);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsInRole("Admin").Returns(true);
        IPaymentService payment = Substitute.For<IPaymentService>();
        payment.RefundPaymentAsync("transaction-1", Arg.Any<CancellationToken>())
            .Returns(PaymentResult.Failure("declined"));

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment)
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.Error.Code.Should().Be("Payment.RefundFailed");
        order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public async Task Admin_Cancel_Refunds_Tickets_On_Pending_Order()
    {
        await using ApplicationDbContext context = CreateContext();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Cancellation test hall");
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        Session session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(1),
            DateTime.UtcNow.AddDays(1).AddHours(2), EntityId<Movie>.New(), hall.Id, EntityId<Pricing>.New());
        Order order = Order.Create(Guid.NewGuid(), session, [seat], new Dictionary<EntityId<Seat>, decimal>
        {
            [seat.Id] = 25m
        });
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Orders.Add(order);
        await context.SaveChangesAsync(CancellationToken.None);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsInRole("Admin").Returns(true);
        IPaymentService payment = Substitute.For<IPaymentService>();

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment)
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.Tickets.Should().ContainSingle().Which.TicketStatus.Should().Be(TicketStatus.Refunded);
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetMyOrders_Puts_Active_And_Cancelled_Orders_In_Current_Buckets()
    {
        await using ApplicationDbContext context = CreateContext();
        Guid userId = Guid.NewGuid();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Orders test hall");
        Movie movie = Movie.CreateManual($"Orders test {Guid.NewGuid():N}", "test", 90,
            DateTime.UtcNow.Year, MovieStatus.ComingSoon);
        Session session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddHours(2), movie.Id, hall.Id, EntityId<Pricing>.New());
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        Order active = Order.Create(userId, session, [seat], new() { [seat.Id] = 20m });
        Order cancelled = Order.New(EntityId<Order>.New(), 30m, userId, session.Id);
        cancelled.MarkAsCancelled();
        context.Halls.Add(hall);
        context.Movies.Add(movie);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Orders.AddRange(active, cancelled);
        await context.SaveChangesAsync(CancellationToken.None);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddApplication();
        services.AddOrdersApplication();

        Cinema.Domain.Shared.Result<OrderHistoryVm> result = await new GetMyOrdersQueryHandler(context, currentUser)
            .Handle(new GetMyOrdersQuery(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ActiveOrders.Select(x => x.Id).Should().ContainSingle().Which.Should().Be(active.Id.Value);
        result.Value.ActiveOrders.Single().Tickets.Should().ContainSingle()
            .Which.Price.Should().Be(20m);
        result.Value.ActiveOrders.Single().Tickets.Single().Status.Should().Be(nameof(TicketStatus.Valid));
        result.Value.PastOrders.Select(x => x.Id).Should().ContainSingle().Which.Should().Be(cancelled.Id.Value);
    }

    [Fact]
    public async Task Ticket_Detail_Uses_Order_Ownership_And_Returns_Ticket_Information()
    {
        await using ApplicationDbContext context = CreateContext();
        Guid ownerId = Guid.NewGuid();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Ticket detail hall");
        Movie movie = Movie.CreateManual($"Ticket detail {Guid.NewGuid():N}", "test", 90,
            DateTime.UtcNow.Year, MovieStatus.ComingSoon);
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 3, 1, 3, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            movie.Id, hall.Id, EntityId<Pricing>.New());
        Order order = Order.Create(ownerId, session, [seat], new() { [seat.Id] = 35m });
        context.Halls.Add(hall);
        context.Movies.Add(movie);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Orders.Add(order);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddApplication();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        GetTicketDetailsQueryHandler handler = new(context, currentUser);
        Guid ticketId = order.Tickets.Single().Id.Value;

        currentUser.UserId.Returns(Guid.NewGuid());
        (await handler.Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken))
            .Error.Code.Should().Be("Ticket.AccessDenied");
        currentUser.UserId.Returns(ownerId);
        Cinema.Domain.Shared.Result<Cinema.Application.Orders.Dtos.TicketDto> detail =
            await handler.Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken);
        detail.IsSuccess.Should().BeTrue();
        detail.Value.Id.Should().Be(ticketId);
        detail.Value.Price.Should().Be(35m);
        detail.Value.Status.Should().Be(nameof(TicketStatus.Valid));
        detail.Value.RowLabel.Should().Be("A");
        detail.Value.SeatNumber.Should().Be(3);
    }

    [Fact]
    public async Task Expiration_Cancels_Order_But_Leaves_Ticket_Valid()
    {
        await using ApplicationDbContext context = CreateContext();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Expiration hall");
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            EntityId<Movie>.New(), hall.Id, EntityId<Pricing>.New());
        Order order = Order.Create(Guid.NewGuid(), session, [seat], new() { [seat.Id] = 25m });
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Orders.Add(order);
        context.Entry(order).Property(x => x.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        ISeatLockingService locks = Substitute.For<ISeatLockingService>();

        await new CancelExpiredOrdersJob(context, locks, NullLogger<CancelExpiredOrdersJob>.Instance)
            .Process(TestContext.Current.CancellationToken);

        Order persisted = await context.Orders.Include(x => x.Tickets).SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Cancelled);
        persisted.Tickets.Single().TicketStatus.Should().Be(TicketStatus.Valid);
        await locks.Received(1).UnlockSeatAsync(session.Id.Value, seat.Id.Value, order.UserId,
            Arg.Any<CancellationToken>());
    }

    private static async Task<(Guid OrderId, Guid UserId, Guid SeatId)> SeedCancellationOrderAsync(
        string databaseName, int minutesUntilStart, bool paid = false, bool used = false)
    {
        await using ApplicationDbContext context = CreateContext(databaseName);
        Guid userId = Guid.NewGuid();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Cancellation hall");
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        DateTime start = DateTime.UtcNow.AddMinutes(minutesUntilStart);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            EntityId<Movie>.New(), hall.Id, EntityId<Pricing>.New());
        Order order = Order.Create(userId, session, [seat], new() { [seat.Id] = 25m });
        if (paid) order.MarkAsPaid("transaction-1");
        if (used) order.Tickets.Single().MarkAsUsed();
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Orders.Add(order);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (order.Id.Value, userId, seat.Id.Value);
    }

    private static ApplicationDbContext CreateContext(string? databaseName = null)
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString())
            .Options;
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
