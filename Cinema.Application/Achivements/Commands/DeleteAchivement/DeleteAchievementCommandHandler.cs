using Cinema.Application.Achievements.Commands;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Handlers;

public class DeleteAchievementCommandHandler(IAdminAchievementsService service)
    : IRequestHandler<DeleteAchievementCommand, AchievementDto>
{
    public Task<AchievementDto> Handle(DeleteAchievementCommand cmd, CancellationToken ct)
        => service.DeleteAchievementAsync(cmd.Id, ct);
}