namespace Cinema.Application.Seats.Dtos;

public record UnlockSeatResponse(
    bool Unlocked,
    string Message,
    Guid SessionId,
    Guid SeatId
);
