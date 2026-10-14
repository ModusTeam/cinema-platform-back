using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Application.Orders.Queries.GetUserOrders;
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
    public async Task Cancel_Uses_Order_Session_Cutoff_In_Fresh_Context(
        bool isAdmin, int minutesUntilStart, bool succeeds, string? errorCode)
    {
        string databaseName = Guid.NewGuid().ToString();
        (Guid orderId, Guid userId, _) = await SeedCancellationOrderAsync(databaseName, minutesUntilStart);
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);
        currentUser.IsInRole("Admin").Returns(isAdmin);

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser,
            Substitute.For<IPaymentService>(), Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(orderId), TestContext.Current.CancellationToken);

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

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment, Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(orderId), TestContext.Current.CancellationToken);

        result.IsSuccess.Should().BeTrue();
        await payment.Received(1).RefundPaymentAsync("transaction-1", Arg.Any<CancellationToken>());
        await using ApplicationDbContext verify = CreateContext(databaseName);
        (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Cancelled);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Refunded);
    }

    [Fact]
    public async Task Cancel_Used_Ticket_Returns_InvalidState()
    {
        string databaseName = Guid.NewGuid().ToString();
        (Guid orderId, Guid userId, _) = await SeedCancellationOrderAsync(databaseName, 20, used: true);
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser,
            Substitute.For<IPaymentService>(), Substitute.For<ILoyaltyService>()).Handle(new CancelOrderCommand(orderId), TestContext.Current.CancellationToken);
        result.Error.Code.Should().Be("Order.InvalidState");
        await using ApplicationDbContext verify = CreateContext(databaseName);
        (await verify.Orders.SingleAsync(TestContext.Current.CancellationToken)).Status.Should().Be(OrderStatus.Pending);
        (await verify.Tickets.SingleAsync(TestContext.Current.CancellationToken)).TicketStatus.Should().Be(TicketStatus.Used);
    }

    [Fact]
    public async Task Cancel_Returns_NotFound_Without_Calling_Payment()
    {
        await using ApplicationDbContext context = CreateContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(Guid.NewGuid());
        IPaymentService payment = Substitute.For<IPaymentService>();
        CancelOrderCommandHandler handler = new(context, currentUser, payment, Substitute.For<ILoyaltyService>());

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

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment, Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.Error.Code.Should().Be("Order.NotFound");
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

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment, Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.Error.Code.Should().Be("Order.AlreadyCancelled");
    }

    [Fact]
    public async Task Cancel_Does_Not_Refund_Paid_Order_Without_Tickets()
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

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment, Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.Error.Code.Should().Be("Order.InvalidState");
        await payment.DidNotReceiveWithAnyArgs().RefundPaymentAsync(default!, CancellationToken.None);
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
        Order order = OrderTicketFixture.CreateAndTrack(context, Guid.NewGuid(), session, [seat], new Dictionary<EntityId<Seat>, decimal>
        {
            [seat.Id] = 25m
        });
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(CancellationToken.None);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.IsInRole("Admin").Returns(true);
        IPaymentService payment = Substitute.For<IPaymentService>();

        Cinema.Domain.Shared.Result result = await new CancelOrderCommandHandler(context, currentUser, payment, Substitute.For<ILoyaltyService>())
            .Handle(new CancelOrderCommand(order.Id.Value), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        order.Status.Should().Be(OrderStatus.Cancelled);
        (await OrderTicketFixture.ForOrder(context, order).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Refunded);
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
        Order active = OrderTicketFixture.CreateAndTrack(context, userId, session, [seat], new() { [seat.Id] = 20m });
        Order cancelled = Order.New(EntityId<Order>.New(), 30m, userId, session.Id);
        cancelled.MarkAsCancelled();
        context.Halls.Add(hall);
        context.Movies.Add(movie);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Orders.Add(cancelled);
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
    public async Task GetMyOrders_Requires_Authentication_And_Returns_Empty_History()
    {
        await using ApplicationDbContext context = CreateContext();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        GetMyOrdersQueryHandler handler = new(context, currentUser);

        (await handler.Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken))
            .Error.Code.Should().Be("Auth.Required");
        currentUser.UserId.Returns(Guid.NewGuid());
        Cinema.Domain.Shared.Result<OrderHistoryVm> empty =
            await handler.Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        empty.IsSuccess.Should().BeTrue();
        empty.Value.ActiveOrders.Should().BeEmpty();
        empty.Value.PastOrders.Should().BeEmpty();
    }

    [Fact]
    public void Ticket_OrderId_Batch_Filter_Translates_For_Postgres()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql("Host=localhost;Database=cinema_orders_test_translation;Username=test;Password=test",
                npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        using ApplicationDbContext context = new(options);
        EntityId<Order>[] orderIds = [EntityId<Order>.New(), EntityId<Order>.New()];

        string sql = context.Tickets.AsNoTracking()
            .Where(ticket => orderIds.Contains(ticket.OrderId))
            .ToQueryString();

        sql.Should().Contain("tickets").And.Contain("order_id");
        sql.Should().Contain("is_active");
    }

    [Fact]
    public async Task History_Composes_Only_Selected_Orders_Tickets_And_Preserves_Dto_Values()
    {
        string databaseName = Guid.NewGuid().ToString();
        Guid userId = Guid.NewGuid();
        Guid otherUserId = Guid.NewGuid();
        DateTime start = DateTime.UtcNow.AddDays(3);
        await using ApplicationDbContext seed = CreateContext(databaseName);
        Hall hall = Hall.Create(EntityId<Hall>.New(), "History hall");
        Movie movie = Movie.CreateManual($"History {Guid.NewGuid():N}", "test", 90,
            DateTime.UtcNow.Year, MovieStatus.ComingSoon);
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Premium", null);
        Seat firstSeat = Seat.New(EntityId<Seat>.New(), "B", 1, 2, 1, SeatStatus.Active, hall.Id, seatType.Id);
        Seat secondSeat = Seat.New(EntityId<Seat>.New(), "B", 2, 2, 2, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([firstSeat, secondSeat]);
        Session earlier = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            movie.Id, hall.Id, EntityId<Pricing>.New());
        Session later = Session.Create(EntityId<Session>.New(), start.AddDays(1), start.AddDays(1).AddHours(2),
            movie.Id, hall.Id, EntityId<Pricing>.New());
        DateTime pastStart = DateTime.UtcNow.AddDays(-2);
        Session pastSession = Session.Create(EntityId<Session>.New(), start.AddDays(2), start.AddDays(2).AddHours(2),
            movie.Id, hall.Id, EntityId<Pricing>.New());
        Order first = OrderTicketFixture.CreateAndTrack(seed, userId, earlier, [firstSeat, secondSeat],
            new() { [firstSeat.Id] = 25m, [secondSeat.Id] = 35m });
        first.ApplyLoyaltyDiscount(10);
        Order second = OrderTicketFixture.CreateAndTrack(seed, userId, later, [firstSeat], new() { [firstSeat.Id] = 40m });
        Order cancelled = Order.New(EntityId<Order>.New(), 15m, userId, earlier.Id);
        cancelled.MarkAsCancelled();
        Order failed = Order.New(EntityId<Order>.New(), 16m, userId, later.Id);
        failed.MarkAsFailed();
        Order historical = Order.New(EntityId<Order>.New(), 17m, userId, pastSession.Id);
        Order foreign = OrderTicketFixture.CreateAndTrack(seed, otherUserId, earlier, [firstSeat], new() { [firstSeat.Id] = 99m });
        seed.Halls.Add(hall);
        seed.Movies.Add(movie);
        seed.SeatTypes.Add(seatType);
        seed.Sessions.AddRange(earlier, later, pastSession);
        seed.Entry(pastSession).Property(session => session.StartTime).CurrentValue = pastStart;
        seed.Orders.AddRange(cancelled, failed, historical);
        await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddApplication();
        services.AddOrdersApplication();
        await using ApplicationDbContext context = CreateContext(databaseName);
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        currentUser.UserId.Returns(userId);

        Cinema.Domain.Shared.Result<OrderHistoryVm> mine = await new GetMyOrdersQueryHandler(context, currentUser)
            .Handle(new GetMyOrdersQuery(), TestContext.Current.CancellationToken);
        mine.IsSuccess.Should().BeTrue();
        mine.Value.ActiveOrders.Select(order => order.Id).Should().Equal(first.Id.Value, second.Id.Value);
        mine.Value.PastOrders.Select(order => order.Id).Should().Equal(failed.Id.Value, cancelled.Id.Value,
            historical.Id.Value);
        mine.Value.ActiveOrders.Concat(mine.Value.PastOrders).Select(order => order.Id).Should().OnlyHaveUniqueItems();
        mine.Value.ActiveOrders[0].Tickets.Select(ticket => ticket.Id)
            .Should().BeEquivalentTo(OrderTicketFixture.ForOrder(context, first).Select(ticket => ticket.Id.Value));
        mine.Value.ActiveOrders[1].Tickets.Should().ContainSingle();
        mine.Value.PastOrders.Should().OnlyContain(order => order.Tickets.Count == 0);
        mine.Value.ActiveOrders[0].TotalAmount.Should().Be(60m);
        mine.Value.ActiveOrders[0].PointsUsed.Should().Be(10);
        mine.Value.ActiveOrders[0].AmountPaid.Should().Be(50m);
        mine.Value.ActiveOrders[0].CreatedAt.Should().Be(first.BookingDate);
        mine.Value.ActiveOrders[0].Status.Should().Be(nameof(OrderStatus.Pending));
        Cinema.Application.Orders.Dtos.TicketDto ticketDto = mine.Value.ActiveOrders[0].Tickets
            .Single(ticket => ticket.SeatNumber == 1);
        ticketDto.Price.Should().Be(25m);
        ticketDto.Status.Should().Be(nameof(TicketStatus.Valid));
        ticketDto.MovieTitle.Should().Be(movie.Title);
        ticketDto.PosterUrl.Should().Be(movie.PosterUrl);
        ticketDto.HallName.Should().Be(hall.Name);
        ticketDto.SessionStart.Should().Be(start);
        ticketDto.RowLabel.Should().Be("B");
        ticketDto.SeatType.Should().Be(seatType.Name);
        ticketDto.SecretCode.Should().Be(ticketDto.Id.ToString()[..8].ToUpperInvariant());

        Cinema.Domain.Shared.Result<List<Cinema.Application.Orders.Dtos.OrderDto>> admin =
            await new GetUserOrdersQueryHandler(context).Handle(new GetUserOrdersQuery(userId),
                TestContext.Current.CancellationToken);
        admin.IsSuccess.Should().BeTrue();
        admin.Value.Select(order => order.Id).Should().BeEquivalentTo(new[]
            { first.Id.Value, second.Id.Value, cancelled.Id.Value, failed.Id.Value, historical.Id.Value });
        admin.Value.Select(order => order.CreatedAt).Should().BeInDescendingOrder();
        admin.Value.Single(order => order.Id == first.Id.Value).Tickets.Should().HaveCount(2);
        admin.Value.Should().OnlyContain(order => order.Id != foreign.Id.Value);
        (await new GetUserOrdersQueryHandler(context).Handle(new GetUserOrdersQuery(Guid.NewGuid()),
            TestContext.Current.CancellationToken)).Value.Should().BeEmpty();
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
        Order order = OrderTicketFixture.CreateAndTrack(context, ownerId, session, [seat], new() { [seat.Id] = 35m });
        context.Halls.Add(hall);
        context.Movies.Add(movie);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Microsoft.Extensions.DependencyInjection.ServiceCollection services = new();
        services.AddApplication();
        ICurrentUserService currentUser = Substitute.For<ICurrentUserService>();
        GetTicketDetailsQueryHandler handler = new(context, currentUser);
        Guid ticketId = (await OrderTicketFixture.ForOrder(context, order)
            .SingleAsync(TestContext.Current.CancellationToken)).Id.Value;

        currentUser.UserId.Returns(Guid.NewGuid());
        (await handler.Handle(new GetTicketDetailsQuery(ticketId), TestContext.Current.CancellationToken))
            .Error.Code.Should().Be("Ticket.NotFound");
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
    public async Task Expiration_Cancels_Order_And_Refunds_Ticket()
    {
        await using ApplicationDbContext context = CreateContext();
        Hall hall = Hall.Create(EntityId<Hall>.New(), "Expiration hall");
        SeatType seatType = SeatType.New(EntityId<SeatType>.New(), "Standard", null);
        Seat seat = Seat.New(EntityId<Seat>.New(), "A", 1, 1, 1, SeatStatus.Active, hall.Id, seatType.Id);
        hall.ApplyLayout([seat]);
        DateTime start = DateTime.UtcNow.AddDays(2);
        Session session = Session.Create(EntityId<Session>.New(), start, start.AddHours(2),
            EntityId<Movie>.New(), hall.Id, EntityId<Pricing>.New());
        Order order = OrderTicketFixture.CreateAndTrack(context, Guid.NewGuid(), session, [seat], new() { [seat.Id] = 25m });
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
        context.Entry(order).Property(x => x.BookingDate).CurrentValue = DateTime.UtcNow.AddHours(-1);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new CancelExpiredOrdersJob(context, NullLogger<CancelExpiredOrdersJob>.Instance)
            .Process(TestContext.Current.CancellationToken);

        Order persisted = await context.Orders.SingleAsync(TestContext.Current.CancellationToken);
        persisted.Status.Should().Be(OrderStatus.Cancelled);
        (await OrderTicketFixture.ForOrder(context, persisted).SingleAsync(TestContext.Current.CancellationToken))
            .TicketStatus.Should().Be(TicketStatus.Refunded);
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
        Order order = OrderTicketFixture.CreateAndTrack(context, userId, session, [seat], new() { [seat.Id] = 25m });
        if (paid) order.MarkAsPaid("transaction-1");
        if (used) OrderTicketFixture.TrackedForOrder(context, order).Single().MarkAsUsed();
        context.Halls.Add(hall);
        context.SeatTypes.Add(seatType);
        context.Sessions.Add(session);
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
