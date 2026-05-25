using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Commands;

public record CreateAchievementCommand(
    string Code, string Name, string Description,
    string? SecretHint, bool IsSecret, string Icon,
    AchievementCategory Category, 
    AchievementRarity Rarity,     
    AchievementStrategy Strategy, 
    string CriteriaJson, int RewardPoints,
    int SortOrder, bool IsActive
) : IRequest<AchievementDto>;