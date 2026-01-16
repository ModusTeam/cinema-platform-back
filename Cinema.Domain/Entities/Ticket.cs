using Cinema.Domain.Common;
using Cinema.Domain.Enums;

namespace Cinema.Domain.Entities;

public class Ticket
{
    public EntityId<Ticket> Id { get; }
    public decimal PriceSnapshot { get; private set; }
    public TicketStatus TicketStatus { get; private set; }

    public EntityId<Order> OrderId { get; private set; }
    public Order? Order { get; private set; }

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
}