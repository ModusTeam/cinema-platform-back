namespace Cinema.Application.Common.Models.Booking;

public record SeatLockState(Guid SeatId, bool IsLockedByCurrentUser);
