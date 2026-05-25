using Cinema.Application.Achievements.Dtos;
using MediatR;

namespace Cinema.Application.Achievements.Commands;

public record DeleteAchievementCommand(string Id) : IRequest<AchievementDto>;