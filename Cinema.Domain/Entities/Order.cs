using Cinema.Domain.Common;
using Cinema.Domain.Enums;

namespace Cinema.Domain.Entities;

public class Order : BaseEntity
{
    public decimal TotalAmount { get; set; }
    public OrderStatus Status { get; set; }
    public DateTime BookingDate { get; set; } = DateTime.UtcNow;
    
    public string? PaymentTransactionId { get; set; } 

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();
}