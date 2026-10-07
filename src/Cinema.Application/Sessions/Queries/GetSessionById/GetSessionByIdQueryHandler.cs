using Cinema.Application.Common.Interfaces;
using Cinema.Application.Sessions.Dtos;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using Mapster;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Queries.GetSessionById;

public class GetSessionByIdQueryHandler(
    IApplicationDbContext context,
    ISeatLockingService seatLockingService)
    : IRequestHandler<GetSessionByIdQuery, Result<SessionDto>>
{
    public async Task<Result<SessionDto>> Handle(GetSessionByIdQuery request, CancellationToken ct)
    {
        var sessionId = new EntityId<Session>(request.Id);
        
        var sessionDto = await context.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .ProjectToType<SessionDto>() 
            .FirstOrDefaultAsync(ct);

        if (sessionDto == null) return Result.Failure<SessionDto>(new Error("404", "Not Found"));
        
        var soldSeats = await context.Tickets
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId && 
                        (t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used))
            .Select(t => t.SeatId.Value)
            .ToListAsync(ct);

        var lockedSeats = await seatLockingService.GetLockedSeatsBySessionAsync(request.Id, ct);

        var allOccupied = soldSeats.Union(lockedSeats).Distinct().ToList();

        return Result.Success(sessionDto with { OccupiedSeatIds = allOccupied });
    }
}