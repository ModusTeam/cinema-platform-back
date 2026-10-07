using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionsByDateQuery;

public class GetSessionsByDateQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetSessionsByDateQuery, Result<List<SessionDto>>>
{
    public async Task<Result<List<SessionDto>>> Handle(GetSessionsByDateQuery request, CancellationToken cancellationToken)
    {
        var date = request.Date?.Date ?? DateTime.UtcNow.Date;
        var nextDay = date.AddDays(1);

        var sessions = await context.Sessions
            .AsNoTracking()
            .Where(s => s.StartTime >= date && s.StartTime < nextDay)
            .OrderBy(s => s.StartTime)
            .ProjectToType<SessionDto>()
            .ToListAsync(cancellationToken);

        return Result.Success(sessions);
    }
}