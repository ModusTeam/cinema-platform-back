using Cinema.Domain.Common;
using Cinema.Domain.Enums;

namespace Cinema.Application.Sessions.Queries.GetSessionSeats;

public record SessionSeatsVm(
    Guid SessionId,
    Guid HallId,
    string MovieTitle,
    DateTime StartTime,
    List<SeatDto> Seats
);

public record SeatDto(
    Guid Id,
    int Row,
    int Number,
    string RowLabel,
    int GridX,
    int GridY,
    string Type,
    decimal Price,
    bool IsAvailable,
    bool IsSold,
    bool IsLocked
);