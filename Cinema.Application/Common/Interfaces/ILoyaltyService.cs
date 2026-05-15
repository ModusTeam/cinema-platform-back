using Cinema.Domain.Shared;

namespace Cinema.Application.Common.Interfaces;

public interface ILoyaltyService
{
    Task<(int Points, string Tier)> GetUserLoyaltyAsync(Guid userId, CancellationToken ct = default);
}