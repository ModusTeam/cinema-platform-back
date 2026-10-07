using Cinema.Application;
using Cinema.Application.Common.Models.DomainEventNotification;
using Cinema.Application.Orders.Commands.CancelOrder;
using Cinema.Application.Orders.Commands.CreateOrder;
using Cinema.Application.Orders.EventHandlers;
using Cinema.Application.Orders.Queries.GetMyOrders;
using Cinema.Application.Orders.Queries.GetUserOrders;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Events;
using Cinema.Domain.Common;
using Cinema.Orders.Application;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Cinema.OrdersTests;

public class OrderBaselineTests
{
    [Fact]
    public void Orders_Registration_Has_One_Order_Handler_Validator_And_Each_Event_Handler()
    {
        ServiceCollection services = new();
        services.AddApplication();
        services.AddOrdersApplication();

        services.Count(x => x.ServiceType == typeof(IRequestHandler<CreateOrderCommand, Cinema.Domain.Shared.Result<Guid>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IRequestHandler<CancelOrderCommand, Cinema.Domain.Shared.Result>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IRequestHandler<GetMyOrdersQuery, Cinema.Domain.Shared.Result<OrderHistoryVm>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IRequestHandler<GetUserOrdersQuery, Cinema.Domain.Shared.Result<List<Cinema.Application.Orders.Dtos.OrderDto>>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(IValidator<CreateOrderCommand>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(INotificationHandler<OrderPaidEvent>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(INotificationHandler<DomainEventNotification<OrderPaidEvent>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(INotificationHandler<DomainEventNotification<OrderFailedDomainEvent>>)).Should().Be(1);
        services.Count(x => x.ServiceType == typeof(INotificationHandler<DomainEventNotification<OrderCancelledDomainEvent>>)).Should().Be(1);
    }

    [Fact]
    public void Create_Order_Validator_Rejects_Duplicate_Seats_And_Empty_Payment_Token()
    {
        Guid seatId = Guid.NewGuid();
        CreateOrderCommand command = new() { SessionId = Guid.NewGuid(), SeatIds = [seatId, seatId] };

        CreateOrderValidator validator = new();
        FluentValidation.Results.ValidationResult result = validator.Validate(command);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(x => x.PropertyName == nameof(CreateOrderCommand.SeatIds));
        result.Errors.Should().Contain(x => x.PropertyName == nameof(CreateOrderCommand.PaymentToken));
    }

    [Fact]
    public void Order_State_Methods_Keep_Existing_Transitions_And_Events()
    {
        Order order = Order.New(EntityId<Order>.New(), 20m, Guid.NewGuid(), EntityId<Session>.New());
        order.Status.Should().Be(OrderStatus.Pending);

        order.MarkAsPaid("transaction");
        order.MarkAsPaid("another transaction");

        order.Status.Should().Be(OrderStatus.Paid);
        order.PaymentTransactionId.Should().Be("transaction");
        order.DomainEvents.OfType<OrderPaidEvent>().Should().ContainSingle();

        order.MarkAsCancelled();
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.DomainEvents.OfType<OrderCancelledDomainEvent>().Should().ContainSingle();
    }
}
