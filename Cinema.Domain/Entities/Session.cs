using Cinema.Domain.Common;
using Cinema.Domain.Enums;

namespace Cinema.Domain.Entities;

public class Session : BaseEntity
{
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public SessionStatus Status { get; set; }

    public Guid MovieId { get; set; }
    public Movie Movie { get; set; } = null!;

    public Guid HallId { get; set; }
    public Hall Hall { get; set; } = null!;

    public ICollection<SessionPricing> SessionPricings { get; set; } = new List<SessionPricing>();
    
    public ICollection<Ticket> Tickets { get; set; } = new List<Ticket>();
}