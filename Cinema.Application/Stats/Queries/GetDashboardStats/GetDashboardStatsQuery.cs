using Cinema.Domain.Shared;
using MediatR;

namespace Cinema.Application.Stats.Queries.GetDashboardStats;

public record GetDashboardStatsQuery(DateTime? From, DateTime? To) 
    : IRequest<Result<Dtos.DashboardStatsDto>>;