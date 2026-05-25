using Cinema.Application.Common.Interfaces;
using Cinema.Infrastructure.Grpc.Loyalty;
using Cinema.Infrastructure.Options;
using Grpc.Core;
using Mapster;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using AppDto = Cinema.Application.Achievements.Dtos.AchievementDto;
using AppCreateReq = Cinema.Application.Achievements.Dtos.CreateAchievementDto;
using AppUpdateReq = Cinema.Application.Achievements.Dtos.UpdateAchievementDto;
using AppAdminResp = Cinema.Application.Achievements.Dtos.GetAdminAchievementsResponse;
using AppUserResp = Cinema.Application.Achievements.Dtos.GetUserAchievementsResponse;

namespace Cinema.Infrastructure.Services;

public class GrpcAchievementsService(
    AchievementsService.AchievementsServiceClient client,
    ILogger<GrpcAchievementsService> logger,
    IOptions<LoyaltySettings> loyaltyOptions) : IAdminAchievementsService
{
    private readonly string _apiKey = loyaltyOptions.Value.ApiKey
        ?? throw new InvalidOperationException("LoyaltySettings:ApiKey is not configured.");

    private Metadata BuildMetadata() => new() { { "x-api-key", _apiKey } };

    public async Task<AppDto> CreateAchievementAsync(
        AppCreateReq request, CancellationToken ct = default)
    {
        try
        {
            var grpcRequest = request.Adapt<CreateAchievementRequest>();
            var response = await client.CreateAchievementAsync(
                grpcRequest, headers: BuildMetadata(), cancellationToken: ct);
            return response.Achievement.Adapt<AppDto>();
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "gRPC CreateAchievement failed. Code: {Code}", ex.StatusCode);
            throw new InvalidOperationException(ex.Status.Detail);
        }
    }

    public async Task<AppDto> UpdateAchievementAsync(
        AppUpdateReq request, CancellationToken ct = default)
    {
        try
        {
            var grpcRequest = request.Adapt<UpdateAchievementRequest>();
            var response = await client.UpdateAchievementAsync(
                grpcRequest, headers: BuildMetadata(), cancellationToken: ct);
            return response.Achievement.Adapt<AppDto>();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new KeyNotFoundException(ex.Status.Detail);
        }
        catch (RpcException ex)
        {
            logger.LogError(ex, "gRPC UpdateAchievement failed.");
            throw new InvalidOperationException(ex.Status.Detail);
        }
    }

    public async Task<AppDto> DeleteAchievementAsync(
        string id, CancellationToken ct = default)
    {
        try
        {
            var response = await client.DeleteAchievementAsync(
                new DeleteAchievementRequest { Id = id },
                headers: BuildMetadata(), cancellationToken: ct);
            return response.Achievement.Adapt<AppDto>();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new KeyNotFoundException(ex.Status.Detail);
        }
    }

    public async Task<AppAdminResp> GetAdminAchievementsAsync(
        bool includeInactive, int limit, int offset, CancellationToken ct = default)
    {
        var response = await client.GetAdminAchievementsAsync(
            new GetAdminAchievementsRequest
            {
                IncludeInactive = includeInactive,
                Limit = limit,
                Offset = offset
            },
            headers: BuildMetadata(), cancellationToken: ct);
        return response.Adapt<AppAdminResp>();
    }

    public async Task<AppUserResp> GetUserAchievementsAsync(
        Guid userId, bool includeLocked, CancellationToken ct = default)
    {
        var response = await client.GetUserAchievementsAsync(
            new GetUserAchievementsRequest
            {
                UserId = userId.ToString(),
                IncludeLocked = includeLocked
            },
            headers: BuildMetadata(), cancellationToken: ct);
        return response.Adapt<AppUserResp>();
    }
}