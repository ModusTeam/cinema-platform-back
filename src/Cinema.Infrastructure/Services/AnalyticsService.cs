using Cinema.Application.Common.Interfaces;
using Cinema.Application.Stats.Queries.Dtos;
using Cinema.Domain.Enums;
using Cinema.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Infrastructure.Services;

public class AnalyticsService(ApplicationDbContext context) : IAnalyticsService
{
    public async Task<KpiStatsDto> GetKpiStatsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var stats = await context.Tickets
            .AsNoTracking()
            .Where(t => t.Session!.StartTime >= from && t.Session.StartTime <= to)
            .Where(t => t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used)
            .GroupBy(x => 1)
            .Select(g => new
            {
                Revenue = g.Sum(t => t.PriceSnapshot),
                Count = g.Count()
            })
            .FirstOrDefaultAsync(ct);

        return new KpiStatsDto(stats?.Revenue ?? 0, stats?.Count ?? 0);
    }

    public async Task<int> GetActiveMoviesCountAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        return await context.Sessions
            .AsNoTracking()
            .Where(s => s.StartTime >= from && s.StartTime <= to)
            .Select(s => s.MovieId)
            .Distinct()
            .CountAsync(ct);
    }

    public async Task<double> GetOccupancyRateAsync(DateTime from, DateTime to, int ticketsSold, CancellationToken ct)
    {
        var totalCapacity = await context.Sessions
            .AsNoTracking()
            .Where(s => s.StartTime >= from && s.StartTime <= to)
            .SumAsync(s => s.Hall!.Seats.Count, ct);

        if (totalCapacity == 0) return 0;
        return Math.Round((double)ticketsSold / totalCapacity * 100, 2);
    }

    public async Task<List<DailySalesDto>> GetSalesChartAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var data = await context.Tickets
            .AsNoTracking()
            .Where(t => t.Session!.StartTime >= from && t.Session.StartTime <= to)
            .Where(t => t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used)
            .GroupBy(t => t.Session!.StartTime.Date)
            .Select(g => new 
            {
                Date = g.Key,
                Revenue = g.Sum(t => t.PriceSnapshot),
                Count = g.Count()
            })
            .OrderBy(x => x.Date)
            .ToListAsync(ct);

        return data.Select(x => new DailySalesDto(x.Date, x.Revenue, x.Count)).ToList();
    }

    public async Task<List<TopMovieDto>> GetTopMoviesAsync(DateTime from, DateTime to, int count, CancellationToken ct)
    {
        var data = await context.Tickets
            .AsNoTracking()
            .Where(t => t.Session!.StartTime >= from && t.Session.StartTime <= to)
            .Where(t => t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used)
            .GroupBy(t => t.Session!.MovieId)
            .Select(g => new 
            {
                MovieTitle = g.First().Session!.Movie!.Title, 
                TicketsSold = g.Count(),
                Revenue = g.Sum(t => t.PriceSnapshot)
            })
            .OrderByDescending(x => x.TicketsSold)
            .Take(count)
            .ToListAsync(ct);

        return data.Select(x => new TopMovieDto(x.MovieTitle, x.TicketsSold, x.Revenue)).ToList();
    }

    public OccupancyLevel CalculateOccupancyLevel(double rate)
    {
        return rate switch
        {
            < 10 => OccupancyLevel.Critical,
            < 30 => OccupancyLevel.Low,
            < 70 => OccupancyLevel.Moderate,
            < 90 => OccupancyLevel.High,
            _ => OccupancyLevel.Full
        };
    }
}
