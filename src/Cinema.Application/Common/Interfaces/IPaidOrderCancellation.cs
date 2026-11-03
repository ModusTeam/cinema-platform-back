using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IPaidOrderCancellation
{
    Task<Result> CancelAsync(Guid orderId, Guid? actorId, bool isAdmin, CancellationToken ct);
}
