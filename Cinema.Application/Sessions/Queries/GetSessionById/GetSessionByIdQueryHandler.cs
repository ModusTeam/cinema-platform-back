using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos; 
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionById;

public class GetSessionByIdQueryHandler : IRequestHandler<GetSessionByIdQuery, Result<SessionDto>>
{
    private readonly IApplicationDbContext _context;

    public GetSessionByIdQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<SessionDto>> Handle(GetSessionByIdQuery request, CancellationToken cancellationToken)
    {
        var sessionId = new EntityId<Session>(request.Id);

        var session = await _context.Sessions
            .AsNoTracking()
            .Include(s => s.Movie)
            .Include(s => s.Hall)
            .Include(s => s.Pricing)
            .FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

        if (session == null)
        {
            return Result.Failure<SessionDto>(new Error("Session.NotFound", "Session not found"));
        }
        
        var dto = new SessionDto
        {
            Id = session.Id.Value,
            StartTime = session.StartTime,
            EndTime = session.EndTime,
            Status = session.Status.ToString(),
            MovieId = session.MovieId.Value,
            MovieTitle = session.Movie?.Title ?? "Unknown Movie",
            HallId = session.HallId.Value,
            HallName = session.Hall?.Name ?? "Unknown Hall",
            PricingId = session.PricingId.Value,
            PricingName = session.Pricing?.Name ?? "No Pricing"

        };

        return Result.Success(dto);
    }
}