using Cinema.Application.Achievements.Queries;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Handlers;

public class GetUserAchievementsQueryHandler(IAdminAchievementsService service)
    : IRequestHandler<GetUserAchievementsQuery, GetUserAchievementsResponse>
{
    public Task<GetUserAchievementsResponse> Handle(GetUserAchievementsQuery query, CancellationToken ct)
        => service.GetUserAchievementsAsync(query.UserId, query.IncludeLocked, ct);
}