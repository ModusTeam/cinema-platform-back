using Cinema.Application.Common.Interfaces;
using Cinema.Infrastructure.Grpc.Loyalty;

namespace Cinema.Infrastructure.Services;

public class GrpcLoyaltyService(LoyaltyService.LoyaltyServiceClient client) : ILoyaltyService
{
    public async Task<(int Points, string Tier)> GetUserLoyaltyAsync(Guid userId, CancellationToken ct = default)
    {
        try
        {
            var request = new GetBalanceRequest { UserId = userId.ToString() };
            var response = await client.GetBalanceAsync(request, cancellationToken: ct);
            return (response.Points, response.Tier.ToString()); 
        }
        catch
        {
            return (0, "BRONZE");
        }
    }
}