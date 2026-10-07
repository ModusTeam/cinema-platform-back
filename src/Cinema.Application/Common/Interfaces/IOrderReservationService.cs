using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IOrderReservationService
{
    Task<Result<Guid>> ReserveOrderAsync(Guid userId, Guid sessionId, List<Guid> seatIds, CancellationToken ct);
}