namespace Cinema.Application.Seats.Dtos;

public record LockSeatResponse(
    bool Locked,
    string Message,
    DateTime ExpiresAt,
    Guid SessionId,
    Guid SeatId
);
