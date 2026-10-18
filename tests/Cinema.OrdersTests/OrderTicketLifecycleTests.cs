using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;
using FluentAssertions;
using Xunit;

namespace Cinema.OrdersTests;

public class OrderTicketLifecycleTests
{
    [Fact]
    public void Reservation_Cannot_Be_Used_Or_Refunded_Before_Purchase()
    {
        Order order = NewOrder();
        Ticket ticket = Reserve(order);

        ticket.TicketStatus.Should().Be(TicketStatus.Reserved);
        ((Action)ticket.MarkAsUsed).Should().Throw<DomainException>();
        ((Action)ticket.MarkAsRefunded).Should().Throw<DomainException>();
        ((Action)(() => ticket.ConfirmPurchase(order))).Should().Throw<DomainException>();
        ticket.TicketStatus.Should().Be(TicketStatus.Reserved);
    }

    [Fact]
    public void Confirmed_Purchase_Requires_Owning_Paid_Order()
    {
        Order order = NewOrder();
        Order otherOrder = NewOrder();
        Ticket ticket = Reserve(order);
        otherOrder.MarkAsPaid("other-transaction");

        ((Action)(() => ticket.ConfirmPurchase(otherOrder))).Should().Throw<DomainException>();
        order.MarkAsPaid("transaction");
        ticket.ConfirmPurchase(order);
        ticket.ConfirmPurchase(order);

        ticket.TicketStatus.Should().Be(TicketStatus.Valid);
        ticket.MarkAsUsed();
        ((Action)(() => ticket.ReleaseReservation(order))).Should().Throw<DomainException>();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Released_Reservation_Preserves_History_And_Cannot_Be_Confirmed(bool expired)
    {
        Order order = NewOrder();
        Ticket ticket = Reserve(order);

        if (expired) order.MarkAsExpired();
        else order.MarkAsFailed();
        ticket.ReleaseReservation(order);
        ticket.ReleaseReservation(order);

        ticket.TicketStatus.Should().Be(TicketStatus.Released);
        ((Action)(() => ticket.ConfirmPurchase(order))).Should().Throw<DomainException>();
        ((Action)ticket.MarkAsUsed).Should().Throw<DomainException>();
    }

    [Fact]
    public void Processing_Or_Uncertain_Order_Cannot_Release_Reservation()
    {
        Order order = NewOrder();
        Ticket ticket = Reserve(order);
        ((Action)(() => ticket.ReleaseReservation(order))).Should().Throw<DomainException>();
        order.StartPayment();
        ((Action)(() => ticket.ReleaseReservation(order))).Should().Throw<DomainException>();
        order.MarkPaymentOutcomeUnknown();
        ((Action)(() => ticket.ReleaseReservation(order))).Should().Throw<DomainException>();
        ticket.TicketStatus.Should().Be(TicketStatus.Reserved);
    }

    [Fact]
    public void Processing_And_Unknown_Outcomes_Block_Expiration()
    {
        Order order = NewOrder();
        order.StartPayment();
        order.StartPayment();
        ((Action)order.MarkAsExpired).Should().Throw<DomainException>();

        order.MarkPaymentOutcomeUnknown();
        order.MarkPaymentOutcomeUnknown();
        ((Action)order.MarkAsExpired).Should().Throw<DomainException>();
        ((Action)order.MarkAsCancelled).Should().Throw<DomainException>();

        order.MarkAsPaid("confirmed-transaction");
        order.MarkAsPaid("confirmed-transaction");
        ((Action)(() => order.MarkAsPaid("another-transaction"))).Should().Throw<DomainException>();
        ((Action)order.MarkAsExpired).Should().Throw<DomainException>();
        order.Status.Should().Be(OrderStatus.Paid);
    }

    [Fact]
    public void Terminal_Orders_Cannot_Return_To_Payment()
    {
        Order failed = NewOrder();
        failed.MarkAsFailed();
        failed.MarkAsFailed();
        ((Action)(() => failed.MarkAsPaid("late"))).Should().Throw<DomainException>();
        ((Action)failed.StartPayment).Should().Throw<DomainException>();

        Order expired = NewOrder();
        expired.MarkAsExpired();
        expired.MarkAsExpired();
        ((Action)(() => expired.MarkAsPaid("late"))).Should().Throw<DomainException>();

        Order paid = NewOrder();
        paid.MarkAsPaid("transaction");
        paid.MarkAsCancelled();
        paid.MarkAsCancelled();
        ((Action)(() => paid.MarkAsPaid("transaction"))).Should().Throw<DomainException>();
    }

    private static Order NewOrder() => Order.New(
        EntityId<Order>.New(), 20m, Guid.NewGuid(), EntityId<Session>.New());

    private static Ticket Reserve(Order order) => Ticket.Reserve(
        EntityId<Ticket>.New(), 20m, order.Id, order.SessionId, EntityId<Seat>.New());
}
