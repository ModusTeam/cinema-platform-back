using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface ISeatLockingService
{
    Task<Result> LockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default);
    Task<Result> UnlockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default);
}