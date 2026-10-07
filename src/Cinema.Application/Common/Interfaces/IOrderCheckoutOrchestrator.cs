using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface IOrderCheckoutOrchestrator
{
    Task<Result<Guid>> ProcessCheckoutAsync(
        Guid userId,
        Guid sessionId,
        List<Guid> seatIds,
        Guid orderId,
        bool applyGoldUpgrade,
        bool useLoyaltyPoints,
        string paymentToken,
        CancellationToken ct);
}
