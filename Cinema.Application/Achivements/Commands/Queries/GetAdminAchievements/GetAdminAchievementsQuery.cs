using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Queries;

public record GetAdminAchievementsQuery(
    bool IncludeInactive, int Limit, int Offset
) : IRequest<GetAdminAchievementsResponse>;