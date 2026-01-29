using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionsByDateQuery;

public class GetSessionsByDateQueryHandler 
    : IRequestHandler<GetSessionsByDateQuery, Result<List<SessionDto>>>
{
    private readonly IApplicationDbContext _context;

    public GetSessionsByDateQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<List<SessionDto>>> Handle(GetSessionsByDateQuery request, CancellationToken cancellationToken)
    {
        var date = request.Date?.Date ?? DateTime.UtcNow.Date;
        var nextDay = date.AddDays(1);

        var sessions = await _context.Sessions
            .AsNoTracking()
            .Where(s => s.StartTime >= date && s.StartTime < nextDay)
            .OrderBy(s => s.StartTime)
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
            .ToListAsync(cancellationToken);

        return Result.Success(sessions);
    }
}