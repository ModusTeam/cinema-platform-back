using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

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
    private readonly List<Ticket> _tickets = new();
    public IReadOnlyCollection<Ticket> Tickets => _tickets.AsReadOnly();
    

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
    
    public static Order Create(
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
        foreach (var seat in seats)
        {
            if (!prices.TryGetValue(seat.Id, out var price))
                throw new DomainException($"Price not found for seat {seat.Id}");
            totalAmount += price;
        }

        var orderId = EntityId<Order>.New();
        
        var order = new Order(
            orderId,
            totalAmount,
            DateTime.UtcNow,
            OrderStatus.Pending,
            null,
            userId,
            session.Id
        );
        
        foreach (var seat in seats)
        {
            var ticket = Ticket.New(
                EntityId<Ticket>.New(),
                prices[seat.Id],
                TicketStatus.Valid,
                orderId,
                session.Id,
                seat.Id
            );

            order._tickets.Add(ticket);
        }

        return order;
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