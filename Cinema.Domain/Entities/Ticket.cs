using Cinema.Domain.Common;
using Cinema.Domain.Enums;

namespace Cinema.Domain.Entities;

public class Ticket : BaseEntity
{
    public decimal PriceSnapshot { get; set; }
    public TicketStatus TicketStatus { get; set; }

    public Guid OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;

    public Guid SeatId { get; set; }
    public Seat Seat { get; set; } = null!;
}