using Cinema.Application.Achievements.Dtos;

namespace Cinema.Application.Common.Interfaces;

public interface IAdminAchievementsService
{
    Task<AchievementDto> CreateAchievementAsync(CreateAchievementDto request, CancellationToken ct = default);
    Task<AchievementDto> UpdateAchievementAsync(UpdateAchievementDto request, CancellationToken ct = default);
    Task<AchievementDto> DeleteAchievementAsync(string id, CancellationToken ct = default);
    Task<GetAdminAchievementsResponse> GetAdminAchievementsAsync(bool includeInactive, int limit, int offset, CancellationToken ct = default);
    Task<GetUserAchievementsResponse> GetUserAchievementsAsync(Guid userId, bool includeLocked, CancellationToken ct = default);
}