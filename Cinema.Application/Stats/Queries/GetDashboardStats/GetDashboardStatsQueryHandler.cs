using System.Text.Json;
using Cinema.Application.Common.Interfaces;
using Cinema.Application.Stats.Queries.Dtos;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.Extensions.Caching.Distributed;

namespace Cinema.Application.Stats.Queries.GetDashboardStats;

public class GetDashboardStatsQueryHandler(
    IAnalyticsService analytics,
    IDistributedCache cache) 
    : IRequestHandler<GetDashboardStatsQuery, Result<DashboardStatsDto>>
{
    public async Task<Result<DashboardStatsDto>> Handle(GetDashboardStatsQuery request, CancellationToken ct)
    {
        var currentTo = request.To ?? DateTime.UtcNow;
        var currentFrom = request.From ?? currentTo.AddDays(-30);

        var daysDiff = (currentTo - currentFrom).TotalDays;
        var prevTo = currentFrom;
        var prevFrom = prevTo.AddDays(-daysDiff);

        var cacheKey = $"stats-v2:{currentFrom:yyyyMMdd}-{currentTo:yyyyMMdd}";
        
        var cachedData = await cache.GetStringAsync(cacheKey, ct);
        if (!string.IsNullOrEmpty(cachedData))
        {
            return Result.Success(JsonSerializer.Deserialize<DashboardStatsDto>(cachedData)!);
        }

        var currentKpi = await analytics.GetKpiStatsAsync(currentFrom, currentTo, ct);
        var activeMovies = await analytics.GetActiveMoviesCountAsync(currentFrom, currentTo, ct);
        var occupancy = await analytics.GetOccupancyRateAsync(currentFrom, currentTo, currentKpi.TicketsCount, ct);
        var chart = await analytics.GetSalesChartAsync(currentFrom, currentTo, ct);
        var topMovies = await analytics.GetTopMoviesAsync(currentFrom, currentTo, 5, ct);
        
        var prevKpi = await analytics.GetKpiStatsAsync(prevFrom, prevTo, ct);
        var prevOccupancy = await analytics.GetOccupancyRateAsync(prevFrom, prevTo, prevKpi.TicketsCount, ct);
        
        var resultDto = new DashboardStatsDto(
            currentKpi.Revenue,
            CalculateChange(currentKpi.Revenue, prevKpi.Revenue),
            
            currentKpi.TicketsCount,
            CalculateChange(currentKpi.TicketsCount, prevKpi.TicketsCount),
            activeMovies,
            occupancy,
            Math.Round(occupancy - prevOccupancy, 2),
            analytics.CalculateOccupancyLevel(occupancy),

            chart,
            topMovies
        );
        
        await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(resultDto), new DistributedCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10)
        }, ct);

        return Result.Success(resultDto);
    }

    private static double CalculateChange(decimal current, decimal previous)
    {
        if (previous == 0) return current > 0 ? 100 : 0;
        return Math.Round((double)((current - previous) / previous * 100), 1);
    }
    
    private static double CalculateChange(int current, int previous)
    {
        if (previous == 0) return current > 0 ? 100 : 0;
        return Math.Round((double)(current - previous) / previous * 100, 1);
    }
}
