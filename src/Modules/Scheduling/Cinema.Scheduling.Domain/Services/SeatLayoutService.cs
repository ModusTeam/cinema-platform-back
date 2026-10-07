using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Exceptions;

namespace Cinema.Domain.Services;

public class SeatLayoutService
{
    public List<Seat> GenerateRectangularGrid(int rows, int seatsPerRow, EntityId<Hall> hallId, EntityId<SeatType> defaultSeatTypeId)
    {
        if (rows <= 0 || seatsPerRow <= 0)
            throw new DomainException("Rows and seats per row must be greater than zero.");

        var seats = new List<Seat>();

        for (var row = 1; row <= rows; row++)
        {
            for (var number = 1; number <= seatsPerRow; number++)
            {
                var seatId = EntityId<Seat>.New();
                
                var seat = Seat.New(
                    id: seatId,
                    rowLabel: row.ToString(),
                    number: number,
                    gridX: number,
                    gridY: row,
                    status: SeatStatus.Active,
                    hallId: hallId,
                    seatTypeId: defaultSeatTypeId
                );

                seats.Add(seat);
            }
        }

        return seats;
    }
}