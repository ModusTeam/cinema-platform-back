using Cinema.Domain.Common;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Entities;

public class Seat
{
    public EntityId<Seat> Id { get; }
    public string RowLabel { get; private set; }
    public int Number { get; private set; }
    public int GridX { get; private set; }
    public int GridY { get; private set; }
    public SeatStatus Status { get; private set; }
    
    public EntityId<Hall> HallId { get; private set; }
    public Hall? Hall { get; private set; }

    public EntityId<SeatType> SeatTypeId { get; private set; }
    public SeatType? SeatType { get; private set; }

    private Seat(
        EntityId<Seat> id,
        string rowLabel,
        int number,
        int gridX,
        int gridY,
        SeatStatus status,
        EntityId<Hall> hallId,
        EntityId<SeatType> seatTypeId)
    {
        Id = id;
        RowLabel = rowLabel;
        Number = number;
        GridX = gridX;
        GridY = gridY;
        Status = status;
        HallId = hallId;
        SeatTypeId = seatTypeId;
    }

    public static Seat New(
        EntityId<Seat> id,
        string rowLabel,
        int number,
        int gridX,
        int gridY,
        SeatStatus status,
        EntityId<Hall> hallId,
        EntityId<SeatType> seatTypeId) => new(id, rowLabel, number, gridX, gridY, status, hallId, seatTypeId);
    
    public void ChangeType(EntityId<SeatType> newSeatTypeId)
    {
        SeatTypeId = newSeatTypeId;
    }

    public void MarkAsBroken()
    {
        if (Status == SeatStatus.Broken) return;
        Status = SeatStatus.Broken;
    }

    public void MarkAsActive()
    {
        if (Status == SeatStatus.Active) return;
        Status = SeatStatus.Active;
    }
}