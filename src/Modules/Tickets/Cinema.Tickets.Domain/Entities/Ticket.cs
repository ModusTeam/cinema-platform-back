using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions; 

namespace Cinema.Domain.Entities;

public class Ticket
{
    public EntityId<Ticket> Id { get; }
    public decimal PriceSnapshot { get; private set; }
    public TicketStatus TicketStatus { get; private set; }

    public EntityId<Order> OrderId { get; private set; }

    public EntityId<Session> SessionId { get; private set; }
    public Session? Session { get; private set; }

    public EntityId<Seat> SeatId { get; private set; }
    public Seat? Seat { get; private set; }
    
    private Ticket(EntityId<Ticket> id,
        decimal priceSnapshot,
        TicketStatus ticketStatus,
        EntityId<Order> orderId,
        EntityId<Session> sessionId,
        EntityId<Seat> seatId)
    {
        Id = id;
        PriceSnapshot = priceSnapshot;
        TicketStatus = ticketStatus;
        OrderId = orderId;
        SessionId = sessionId;
        SeatId = seatId;
    }

    public static Ticket New(
        EntityId<Ticket> id,
        decimal priceSnapshot,
        TicketStatus ticketStatus,
        EntityId<Order> orderId,
        EntityId<Session> sessionId,
        EntityId<Seat> seatId) => new(id, priceSnapshot, ticketStatus, orderId, sessionId, seatId);

    public static Ticket Reserve(
        EntityId<Ticket> id,
        decimal priceSnapshot,
        EntityId<Order> orderId,
        EntityId<Session> sessionId,
        EntityId<Seat> seatId) => new(id, priceSnapshot, TicketStatus.Reserved, orderId, sessionId, seatId);

    public void ConfirmPurchase(Order order)
    {
        if (order.Id != OrderId || order.SessionId != SessionId || order.Status != OrderStatus.Paid)
            throw new DomainException("Ticket purchase requires confirmation of its owning order.");
        if (TicketStatus == TicketStatus.Valid)
            return;
        if (TicketStatus != TicketStatus.Reserved)
            throw new DomainException($"Cannot confirm a ticket in {TicketStatus} status.");
        TicketStatus = TicketStatus.Valid;
    }

    public void ReleaseReservation(Order order)
    {
        if (order.Id != OrderId || order.SessionId != SessionId ||
            order.Status is not (OrderStatus.Failed or OrderStatus.Expired or OrderStatus.Cancelled))
            throw new DomainException("Ticket release requires a terminal owning order.");
        if (TicketStatus == TicketStatus.Released)
            return;
        if (TicketStatus != TicketStatus.Reserved)
            throw new DomainException($"Cannot release a ticket in {TicketStatus} status.");
        TicketStatus = TicketStatus.Released;
    }
    

    public void MarkAsRefunded()
    {
        if (TicketStatus == TicketStatus.Refunded)
            return;
        if (TicketStatus != TicketStatus.Valid)
            throw new DomainException($"Cannot refund a ticket in {TicketStatus} status.");
            
        TicketStatus = TicketStatus.Refunded;
    }

    public void MarkAsUsed()
    {
        if (TicketStatus != TicketStatus.Valid)
            throw new DomainException($"Cannot use ticket. Current status: {TicketStatus}");
            
        TicketStatus = TicketStatus.Used;
    }

    public bool IsGoldUpgraded { get; private set; }

    public void RestoreGoldUpgrade(decimal originalPrice)
    {
        PriceSnapshot = originalPrice;
        IsGoldUpgraded = false;
    }

    public void ApplyGoldUpgrade(decimal standardPrice)
    {
        if (TicketStatus is not (TicketStatus.Reserved or TicketStatus.Valid))
            throw new DomainException($"Cannot upgrade a ticket in {TicketStatus} status.");
        if (IsGoldUpgraded)
            throw new DomainException("Ticket is already gold upgraded.");

        PriceSnapshot = standardPrice;
        IsGoldUpgraded = true;
    }
}
