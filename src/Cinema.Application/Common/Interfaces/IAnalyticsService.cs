using Cinema.Application.Stats.Queries.Dtos;
using Cinema.Domain.Enums;

namespace Cinema.Application.Common.Interfaces;

public interface IAnalyticsService
{
    Task<KpiStatsDto> GetKpiStatsAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<int> GetActiveMoviesCountAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<double> GetOccupancyRateAsync(DateTime from, DateTime to, int ticketsSold, CancellationToken ct);
    Task<List<DailySalesDto>> GetSalesChartAsync(DateTime from, DateTime to, CancellationToken ct);
    Task<List<TopMovieDto>> GetTopMoviesAsync(DateTime from, DateTime to, int count, CancellationToken ct);
    OccupancyLevel CalculateOccupancyLevel(double rate);
}

public record KpiStatsDto(decimal Revenue, int TicketsCount);