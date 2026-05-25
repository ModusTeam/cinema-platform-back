// CreateAchievementCommandHandler.cs
using Cinema.Application.Achievements.Commands;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Handlers;

public class CreateAchievementCommandHandler(IAdminAchievementsService service)
    : IRequestHandler<CreateAchievementCommand, AchievementDto>
{
    public Task<AchievementDto> Handle(CreateAchievementCommand cmd, CancellationToken ct)
        => service.CreateAchievementAsync(new CreateAchievementDto
        {
            Code = cmd.Code, Name = cmd.Name, Description = cmd.Description,
            SecretHint = cmd.SecretHint ?? string.Empty, IsSecret = cmd.IsSecret,
            Icon = cmd.Icon, Category = (AchievementCategory)cmd.Category,
            Rarity = (AchievementRarity)cmd.Rarity, Strategy = (AchievementStrategy)cmd.Strategy,
            CriteriaJson = cmd.CriteriaJson, RewardPoints = cmd.RewardPoints,
            SortOrder = cmd.SortOrder, IsActive = cmd.IsActive
        }, ct);
}