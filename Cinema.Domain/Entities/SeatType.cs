using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class SeatType : BaseEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
}