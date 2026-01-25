using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries;

public record GetSessionsByDateQuery(DateTime Date) : IRequest<Result<List<SessionDto>>>;

public class GetSessionsByDateQueryHandler : IRequestHandler<GetSessionsByDateQuery, Result<List<SessionDto>>>
{
    private readonly IApplicationDbContext _context;

    public GetSessionsByDateQueryHandler(IApplicationDbContext context)
    {
        _context = context;
    }

    public async Task<Result<List<SessionDto>>> Handle(GetSessionsByDateQuery request, CancellationToken cancellationToken)
    {
        var startOfDay = DateTime.SpecifyKind(request.Date.Date, DateTimeKind.Utc);
        var endOfDay = startOfDay.AddDays(1).AddTicks(-1);

        var sessions = await _context.Sessions
            .AsNoTracking()
            .Include(s => s.Movie)
            .Include(s => s.Hall)
            .Where(s => s.StartTime >= startOfDay && s.StartTime <= endOfDay)
            .OrderBy(s => s.StartTime)
            .ToListAsync(cancellationToken);

        var dtos = sessions.Select(SessionDto.FromDomainModel).ToList();

        return Result.Success(dtos);
    }
}