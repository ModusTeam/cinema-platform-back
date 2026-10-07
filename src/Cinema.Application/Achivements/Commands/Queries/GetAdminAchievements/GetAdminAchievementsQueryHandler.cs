using Cinema.Application.Achievements.Queries;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Handlers;

public class GetAdminAchievementsQueryHandler(IAdminAchievementsService service)
    : IRequestHandler<GetAdminAchievementsQuery, GetAdminAchievementsResponse>
{
    public Task<GetAdminAchievementsResponse> Handle(GetAdminAchievementsQuery query, CancellationToken ct)
        => service.GetAdminAchievementsAsync(query.IncludeInactive, query.Limit, query.Offset, ct);
}