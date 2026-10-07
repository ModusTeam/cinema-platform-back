using Cinema.Domain.Common;
using Cinema.Domain.Entities;

namespace Cinema.Application.Common.Interfaces;

public interface ISeatTypeProvider
{
    SeatType? Get(EntityId<SeatType> id);
    string GetName(EntityId<SeatType> id);
}