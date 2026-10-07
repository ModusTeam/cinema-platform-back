using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Commands;

public record UpdateAchievementCommand(
    string Id, string Code, string Name, string Description,
    string? SecretHint, bool IsSecret, string Icon,
    int Category, int Rarity, int Strategy,
    string CriteriaJson, int RewardPoints,
    int SortOrder, bool IsActive
) : IRequest<AchievementDto>;