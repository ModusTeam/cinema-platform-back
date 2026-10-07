using Cinema.Domain.Common;
using Cinema.Domain.Entities;

namespace Cinema.Application.Common.Interfaces;

public interface IPriceCalculator
{
    decimal CalculatePrice(
        Pricing pricing, 
        EntityId<SeatType> seatTypeId, 
        DateTime sessionStartTime);
}