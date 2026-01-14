using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Hall : BaseEntity
{
    public required string Name { get; set; }
    public int TotalCapacity { get; set; }
    public string? ScreenTechnology { get; set; }

    public ICollection<Seat> Seats { get; set; } = new List<Seat>();
    public ICollection<Session> Sessions { get; set; } = new List<Session>();
}