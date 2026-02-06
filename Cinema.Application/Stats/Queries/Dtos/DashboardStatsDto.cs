using Cinema.Domain.Enums;

namespace Cinema.Application.Stats.Queries.Dtos;

public record DashboardStatsDto(
    decimal TotalRevenue,
    double RevenueChangePercent,
    int TicketsSold,
    double TicketsChangePercent,
    int ActiveMoviesCount,
    double OccupancyRate,
    double OccupancyChangePercent,
    OccupancyLevel OccupancyLevel,
    
    List<DailySalesDto> SalesChart, 
    List<TopMovieDto> TopMovies
);

public record DailySalesDto(DateTime Date, decimal Revenue, int TicketsCount);
public record TopMovieDto(string MovieTitle, int TicketsSold, decimal Revenue);