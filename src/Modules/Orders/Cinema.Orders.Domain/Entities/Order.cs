using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Events;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Entities;

public class Order : BaseEntity
{
    public EntityId<Order> Id { get; }
    public decimal TotalAmount { get; private set; }
    public decimal PaidAmount { get; private set; }
    public DateTime BookingDate { get; private set; }
    public OrderStatus Status { get; private set; }
    public string? PaymentTransactionId { get; private set; }

    public Guid UserId { get; private set; }
    public EntityId<Session> SessionId { get; private set; }

    public int PointsUsed { get; private set; }

    public Session? Session { get; private set; }
    

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
        PaidAmount = totalAmount;
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
    
    public static Order CreateWithoutTickets(
        Guid userId,
        Session session,
        List<Seat> seats,
        Dictionary<EntityId<Seat>, decimal> prices)
    {
        if (session.StartTime <= DateTime.UtcNow)
            throw new DomainException("Cannot create order for a started session.");

        if (seats.Any(s => s.HallId != session.HallId))
            throw new DomainException("Seats belong to a different hall.");

        if (seats.Any(s => s.Status != SeatStatus.Active))
            throw new DomainException("One or more seats are not active.");

        decimal totalAmount = 0;
        foreach (Seat seat in seats)
        {
            if (!prices.TryGetValue(seat.Id, out decimal price))
                throw new DomainException($"Price not found for seat {seat.Id}");
            totalAmount += price;
        }

        return New(EntityId<Order>.New(), totalAmount, userId, session.Id);
    }

    public void ApplyLoyaltyDiscount(int pointsUsed)
    {
        if (pointsUsed < 0) throw new ArgumentException("Points cannot be negative");

        decimal discount = pointsUsed;
        if (discount > TotalAmount)
            throw new DomainException($"Discount ({discount}) cannot exceed TotalAmount ({TotalAmount}).");

        PointsUsed = pointsUsed;
        PaidAmount = TotalAmount - discount;
    }

    public void MarkAsPaid(string externalTransactionId)
    {
        if (Status == OrderStatus.Paid)
        {
            if (PaymentTransactionId != externalTransactionId)
                throw new DomainException("Order was paid with a different transaction.");
            return;

        }

        if (Status is not (OrderStatus.Pending or OrderStatus.PaymentProcessing or OrderStatus.PaymentOutcomeUnknown))
            throw new DomainException($"Cannot pay an order in {Status} status.");
        if (string.IsNullOrWhiteSpace(externalTransactionId))
            throw new DomainException("Payment confirmation requires a transaction ID.");

        PaymentTransactionId = externalTransactionId;
        Status = OrderStatus.Paid;
        
        AddDomainEvent(new OrderPaidEvent(this)); 
    }

    public void MarkAsFailed()
    {
        if (Status == OrderStatus.Failed)
            return;

        if (Status is not (OrderStatus.Pending or OrderStatus.PaymentProcessing or OrderStatus.PaymentOutcomeUnknown))
            throw new DomainException($"Cannot fail an order in {Status} status.");

        Status = OrderStatus.Failed;
        AddDomainEvent(new OrderFailedDomainEvent(this));
    }

    public void MarkAsCancelled()
    {
        if (Status == OrderStatus.Cancelled)
            return;

        if (Status is not (OrderStatus.Pending or OrderStatus.Paid))
            throw new DomainException($"Cannot cancel an order in {Status} status.");

        Status = OrderStatus.Cancelled;
        AddDomainEvent(new OrderCancelledDomainEvent(this));
    }

    public void StartPayment()
    {
        if (Status == OrderStatus.PaymentProcessing)
            return;
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot start payment for an order in {Status} status.");
        Status = OrderStatus.PaymentProcessing;
    }

    public void MarkPaymentOutcomeUnknown()
    {
        if (Status == OrderStatus.PaymentOutcomeUnknown)
            return;
        if (Status != OrderStatus.PaymentProcessing)
            throw new DomainException($"Cannot mark payment outcome unknown for an order in {Status} status.");
        Status = OrderStatus.PaymentOutcomeUnknown;
    }

    public void ResetPaymentBeforeCharge()
    {
        if (Status != OrderStatus.PaymentProcessing)
            throw new DomainException($"Cannot reset payment for an order in {Status} status.");
        Status = OrderStatus.Pending;
    }

    public void MarkAsExpired()
    {
        if (Status == OrderStatus.Expired)
            return;
        if (Status != OrderStatus.Pending)
            throw new DomainException($"Cannot expire an order in {Status} status.");
        Status = OrderStatus.Expired;
    }

    public void ApplyGoldDiscount(decimal priceDifference)
    {
        if (priceDifference <= 0 || priceDifference > TotalAmount)
            throw new DomainException("Gold discount must be positive and cannot exceed the order total.");

        TotalAmount -= priceDifference;
        
        if (PaidAmount > TotalAmount)
        {
            PaidAmount = TotalAmount;
        }
    }
}
