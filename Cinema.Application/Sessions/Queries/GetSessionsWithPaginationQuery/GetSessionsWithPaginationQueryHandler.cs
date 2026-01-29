using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionsWithPaginationQuery;

public class GetSessionsWithPaginationQueryHandler 
    : IRequestHandler<GetSessionsWithPaginationQuery, Result<PaginatedList<SessionDto>>>
{
    private readonly IApplicationDbContext _context;

    public GetSessionsWithPaginationQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<PaginatedList<SessionDto>>> Handle(GetSessionsWithPaginationQuery request, CancellationToken cancellationToken)
    {
        var paginatedList = await _context.Sessions
            .AsNoTracking()
            .OrderByDescending(x => x.StartTime)
            .Include(s => s.Movie)
            .Include(s => s.Hall)
            .Include(s => s.Pricing)
            .Select(s => new SessionDto
            {
                Id = s.Id.Value,
                StartTime = s.StartTime,
                EndTime = s.EndTime,
                Status = s.Status.ToString(),
                MovieId = s.MovieId.Value,
                MovieTitle = s.Movie != null ? s.Movie.Title : "Unknown",
                HallId = s.HallId.Value,
                HallName = s.Hall != null ? s.Hall.Name : "Unknown",
                PricingId = s.PricingId.Value,
                PricingName = s.Pricing != null ? s.Pricing.Name : "No Pricing"
            })
            .PaginatedListAsync(request.PageNumber, request.PageSize);

        return Result.Success(paginatedList);
    }
}

public static class MappingExtensions
{
    public static Task<PaginatedList<TDestination>> PaginatedListAsync<TDestination>(
        this IQueryable<TDestination> queryable, int pageNumber, int pageSize) 
        where TDestination : class
    {
        return PaginatedList<TDestination>.CreateAsync(queryable, pageNumber, pageSize);
    }
}