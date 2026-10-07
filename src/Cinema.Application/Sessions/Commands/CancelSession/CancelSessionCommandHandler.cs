using Cinema.Application.Common.Interfaces;
using Cinema.Domain.Common;
using Cinema.Domain.Entities;
using Cinema.Domain.Enums;
using Cinema.Domain.Shared;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Cinema.Application.Sessions.Commands.CancelSession;

public class CancelSessionCommandHandler(IApplicationDbContext context) 
    : IRequestHandler<CancelSessionCommand, Result>
{
    public async Task<Result> Handle(CancelSessionCommand request, CancellationToken ct)
    {
        var sessionId = new EntityId<Session>(request.SessionId);

        var session = await context.Sessions
            .Include(s => s.Tickets)
            .FirstOrDefaultAsync(s => s.Id == sessionId, ct);

        if (session == null)
        {
            return Result.Failure(new Error("Session.NotFound", "Session not found"));
        }
        
        var hasSoldTickets = session.Tickets.Any(t => 
            t.TicketStatus == TicketStatus.Valid || t.TicketStatus == TicketStatus.Used);

        if (hasSoldTickets)
        {
            return Result.Failure(new Error("Session.CannotCancel", 
                "Cannot cancel session with sold tickets. Please perform a refund procedure first."));
        }

        try
        {
            session.Cancel(DateTime.UtcNow);
            
            await context.SaveChangesAsync(ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            return Result.Failure(new Error("Session.CancelFailed", ex.Message));
        }
    }
}