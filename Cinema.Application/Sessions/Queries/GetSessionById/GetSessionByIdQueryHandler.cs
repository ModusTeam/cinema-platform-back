using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos; 
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionById;

public class GetSessionByIdQueryHandler(IApplicationDbContext context) 
    : IRequestHandler<GetSessionByIdQuery, Result<SessionDto>>
{
    public async Task<Result<SessionDto>> Handle(GetSessionByIdQuery request, CancellationToken cancellationToken)
    {
        var sessionId = new EntityId<Session>(request.Id);
        
        var sessionDto = await context.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .ProjectToType<SessionDto>() 
            .FirstOrDefaultAsync(cancellationToken);

        if (sessionDto == null)
        {
            return Result.Failure<SessionDto>(new Error("Session.NotFound", "Session not found"));
        }
        
        return Result.Success(sessionDto);
    }
}