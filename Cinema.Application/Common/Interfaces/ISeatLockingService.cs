using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface ISeatLockingService
{
    Task<Result> LockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default);
    Task<Result> UnlockSeatAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default);
    Task<bool> ValidateLockAsync(Guid sessionId, Guid seatId, Guid userId, CancellationToken ct = default);
    Task<IEnumerable<Guid>> GetLockedSeatsAsync(Guid sessionId, IEnumerable<Guid> seatIds, CancellationToken ct = default);
}