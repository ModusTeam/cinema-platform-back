using Cinema.Application;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Common.Models.Payments;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Catalog.Domain.Entities;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using Cinema.Orders.Application;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace Cinema.OrdersTests;

public class OrderHandlersTests
{
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

    [Fact]
    public async Task Cancel_Rejects_Already_Cancelled_Order()
    {
        await using ApplicationDbContext context = CreateContext();
        Guid userId = Guid.NewGuid();
        Order order = Order.New(EntityId<Order>.New(), 25m, userId, EntityId<Session>.New());
        order.MarkAsCancelled();
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
        Session session = Session.Create(EntityId<Session>.New(), DateTime.UtcNow.AddDays(2),
            DateTime.UtcNow.AddDays(2).AddHours(2), EntityId<Movie>.New(), hall.Id, EntityId<Pricing>.New());
        Order active = Order.New(EntityId<Order>.New(), 20m, userId, session.Id);
        Order cancelled = Order.New(EntityId<Order>.New(), 30m, userId, session.Id);
        cancelled.MarkAsCancelled();
        context.Halls.Add(hall);
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
        result.Value.PastOrders.Select(x => x.Id).Should().ContainSingle().Which.Should().Be(cancelled.Id.Value);
    }

    private static ApplicationDbContext CreateContext()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
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
