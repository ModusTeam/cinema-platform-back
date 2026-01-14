using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class SeatLock : BaseEntity
{
    public DateTime ExpiresAt { get; set; }
    
    public Guid SessionId { get; set; }
    public Session Session { get; set; } = null!;

    public Guid SeatId { get; set; }
    public Seat Seat { get; set; } = null!;

    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
}