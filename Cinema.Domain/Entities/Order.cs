using Cinema.Domain.Common;
using Cinema.Domain.Enums;

namespace Cinema.Domain.Entities;

public class Order
{
    public EntityId<Order> Id { get; }
    public decimal TotalAmount { get; private set; }
    public DateTime BookingDate { get; private set; }
    public OrderStatus Status { get; private set; }
    public string? PaymentTransactionId { get; private set; }

    public Guid UserId { get; private set; }
    public EntityId<Session> SessionId { get; private set; }

    public User? User { get; private set; }
    public Session? Session { get; private set; }
    public ICollection<Ticket>? Tickets { get; private set; } = [];

    private Order(
        EntityId<Order> id,
        decimal totalAmount,
        DateTime bookingDate,
        OrderStatus status,
        string? paymentTransactionId,
        Guid userId,
        EntityId<Session> sessionId)
    {
        Id = id;
        TotalAmount = totalAmount;
        BookingDate = bookingDate;
        Status = status;
        PaymentTransactionId = paymentTransactionId;
        UserId = userId;
        SessionId = sessionId;
    }

    public static Order New(
        EntityId<Order> id,
        decimal totalAmount,
        Guid userId,
        EntityId<Session> sessionId)
    {
        return new Order(
            id,
            totalAmount,
            DateTime.UtcNow,
            OrderStatus.Pending,
            null,
            userId,
            sessionId
        );
    }

    public void MarkAsPaid(string externalTransactionId)
    {
        if (Status == OrderStatus.Paid)
            return;

        PaymentTransactionId = externalTransactionId;
        Status = OrderStatus.Paid;
    }

    public void MarkAsFailed()
    {
        Status = OrderStatus.Failed;
    }

    public void MarkAsCancelled()
    {
        Status = OrderStatus.Cancelled;
    }
}