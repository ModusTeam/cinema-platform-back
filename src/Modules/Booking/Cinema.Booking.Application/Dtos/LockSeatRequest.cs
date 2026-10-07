namespace Cinema.Application.Seats.Dtos;

public record LockSeatRequest(Guid SessionId, Guid SeatId);