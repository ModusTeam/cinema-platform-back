using Cinema.Domain.Common;

namespace Cinema.Domain.Entities;

public class Seat : BaseEntity
{
    public int Number { get; set; }
    public string RowLabel { get; set; } = string.Empty;
    public int GridX { get; set; }
    public int GridY { get; set; }
    public bool IsBroken { get; set; }

    public Guid HallId { get; set; }
    public Hall Hall { get; set; } = null!;

    public Guid SeatTypeId { get; set; }
    public SeatType SeatType { get; set; } = null!;
}