using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Queries;

public record GetUserAchievementsQuery(
    Guid UserId, bool IncludeLocked
) : IRequest<GetUserAchievementsResponse>;